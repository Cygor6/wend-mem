using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Serialization;
using Wendmem.Services;

namespace Wendmem.Storage;

public sealed class SkillStorage
{
    readonly DuckDbConnectionFactory _dbFactory;
    readonly IEmbedder _embedder;
    readonly ILogger<SkillStorage> _logger;

    public SkillStorage(
        DuckDbConnectionFactory dbFactory,
        IEmbedder embedder,
        ILogger<SkillStorage> logger)
    {
        _dbFactory = dbFactory;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<SkillEntry> RegisterAsync(
        string folderPath, string? wing, CancellationToken ct)
    {
        var absPath = Path.GetFullPath(folderPath);
        var skillMdPath = Path.Combine(absPath, "SKILL.md");
        var folderName = Path.GetFileName(absPath);

        if (!File.Exists(skillMdPath))
            throw new FileNotFoundException($"SKILL.md not found at {skillMdPath}");

        var content = await File.ReadAllTextAsync(skillMdPath, ct);
        var fm = SkillFrontmatterParser.Parse(content, folderName, skillMdPath);
        var mtime = File.GetLastWriteTimeUtc(skillMdPath).Ticks;

        var id = ComputeId(absPath);
        var embedText = $"{fm.Name}: {fm.Description}";
        float[]? embedding = null;
        if (_embedder.IsAvailable)
        {
            try
            { embedding = await _embedder.EmbedAsync(embedText, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to embed skill {Name} — skill will be invisible to FindSkills until re-embedded", fm.Name); }
        }

        var embLit = embedding is not null ? EmbeddingUtils.ToFloatArrayLiteral(embedding) : null;
        var metadataJson = fm.Metadata is not null
            ? JsonSerializer.Serialize(fm.Metadata, WendmemJsonContext.Default.DictionaryStringString) : null;

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = embLit is not null
                ? """
                  INSERT INTO skills (id, wing, name, description, folder_path, skill_md_path,
                      skill_md_mtime, metadata_json, compatibility, license, embedding)
                  VALUES ($id, $wing, $name, $desc, $folder, $md_path, $mtime, $meta, $compat, $lic, #emb::FLOAT[512])
                  ON CONFLICT (folder_path) DO UPDATE SET
                      name = $name, description = $desc, skill_md_path = $md_path,
                      skill_md_mtime = $mtime, metadata_json = $meta, compatibility = $compat,
                      license = $lic, embedding = #emb::FLOAT[512], wing = $wing
                  """
                : """
                  INSERT INTO skills (id, wing, name, description, folder_path, skill_md_path,
                      skill_md_mtime, metadata_json, compatibility, license, embedding)
                  VALUES ($id, $wing, $name, $desc, $folder, $md_path, $mtime, $meta, $compat, $lic, NULL)
                  ON CONFLICT (folder_path) DO UPDATE SET
                      name = $name, description = $desc, skill_md_path = $md_path,
                      skill_md_mtime = $mtime, metadata_json = $meta, compatibility = $compat,
                      license = $lic, embedding = NULL, wing = $wing
                  """;
            if (embLit is not null)
                cmd.CommandText = cmd.CommandText.Replace("#emb", embLit);
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", (object?)wing ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("name", fm.Name));
            cmd.Parameters.Add(new DuckDBParameter("desc", fm.Description));
            cmd.Parameters.Add(new DuckDBParameter("folder", absPath));
            cmd.Parameters.Add(new DuckDBParameter("md_path", skillMdPath));
            cmd.Parameters.Add(new DuckDBParameter("mtime", mtime));
            cmd.Parameters.Add(new DuckDBParameter("meta", (object?)metadataJson ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("compat", (object?)fm.Compatibility ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("lic", (object?)fm.License ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        _logger.LogInformation("Registered skill {Name} id={Id}", fm.Name, id);
        return new SkillEntry(id, wing, fm.Name, fm.Description, absPath, skillMdPath, mtime,
            metadataJson, fm.Compatibility, fm.License, 0, 0, null, DateTimeOffset.UtcNow);
    }

    public async Task<List<SkillSearchResult>> FindAsync(
        string query, string? wing, int k, CancellationToken ct)
    {
        if (!_embedder.IsAvailable)
            return [];

        var queryEmb = await _embedder.EmbedAsync(query, ct);
        var embLit = EmbeddingUtils.ToFloatArrayLiteral(queryEmb);
        var wingFilter = wing is not null ? "AND wing = $wing" : "";

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, name, description, folder_path,
                   success_count, failure_count,
                   array_cosine_similarity(embedding, #qemb::FLOAT[512]) AS score
            FROM skills
            WHERE embedding IS NOT NULL
              {wingFilter}
            ORDER BY score DESC
            LIMIT $k
            """.Replace("#qemb", embLit);
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("k", k));

        var results = new List<SkillSearchResult>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            var score = reader.GetFloat(7);
            if (score < 0.50f)
                continue;
            var success = reader.GetInt32(5);
            var failure = reader.GetInt32(6);
            var laplace = (float)(success + 1) / (success + failure + 2);
            var boosted = score + 0.10f * laplace;
            results.Add(new SkillSearchResult(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), success, failure, score, boosted));
        }
        return results.OrderByDescending(r => r.BoostedScore).Take(k).ToList();
    }

    public async Task<List<SkillEntry>> ListAsync(string? wing, CancellationToken ct)
    {
        var wingFilter = wing is not null ? "WHERE wing = $wing" : "";
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, name, description, folder_path, skill_md_path,
                   skill_md_mtime, metadata_json, compatibility, license,
                   success_count, failure_count, last_used_at, registered_at
            FROM skills {wingFilter}
            ORDER BY name
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var results = new List<SkillEntry>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
            results.Add(ReadSkill(reader));
        return results;
    }

    public async Task<SkillEntry?> GetByIdOrNameAsync(string idOrName, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, name, description, folder_path, skill_md_path,
                   skill_md_mtime, metadata_json, compatibility, license,
                   success_count, failure_count, last_used_at, registered_at
            FROM skills WHERE id = $id OR name = $name
            LIMIT 1
            """;
        cmd.Parameters.Add(new DuckDBParameter("id", idOrName));
        cmd.Parameters.Add(new DuckDBParameter("name", idOrName));
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        return reader.Read() ? ReadSkill(reader) : null;
    }

    public async Task<bool> RemoveAsync(string idOrName, CancellationToken ct)
    {
        var affected = 0;
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM skills WHERE id = $id OR name = $name";
            cmd.Parameters.Add(new DuckDBParameter("id", idOrName));
            cmd.Parameters.Add(new DuckDBParameter("name", idOrName));
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
        return affected > 0;
    }

    public async Task<int> ReindexAsync(string rootDir, string? wing, CancellationToken ct)
    {
        var added = 0;
        var updated = 0;
        var removed = 0;

        var diskSkills = new Dictionary<string, string>(); // folderPath -> skillMdPath
        foreach (var mdFile in Directory.GetFiles(rootDir, "SKILL.md", SearchOption.AllDirectories))
        {
            var folder = Path.GetDirectoryName(mdFile)!;
            diskSkills[Path.GetFullPath(folder)] = mdFile;
        }

        var dbEntries = await ListAsync(wing, ct);
        var dbByPath = dbEntries.ToDictionary(e => e.FolderPath);

        foreach (var (folder, mdPath) in diskSkills)
        {
            if (dbByPath.TryGetValue(folder, out var existing))
            {
                var mtime = File.GetLastWriteTimeUtc(mdPath).Ticks;
                if (mtime != existing.SkillMdMtime)
                {
                    await RegisterAsync(folder, wing, ct);
                    updated++;
                }
            }
            else
            {
                await RegisterAsync(folder, wing, ct);
                added++;
            }
        }

        foreach (var entry in dbEntries)
        {
            if (!Directory.Exists(entry.FolderPath))
            {
                await RemoveAsync(entry.Id, ct);
                removed++;
            }
        }

        _logger.LogInformation("Reindex: +{Added} added, ~{Updated} updated, -{Removed} removed", added, updated, removed);
        return added + updated + removed;
    }

    static SkillEntry ReadSkill(DuckDBDataReader reader)
    {
        return new SkillEntry(
            Id: reader.GetString(0),
            Wing: reader.IsDBNull(1) ? null : reader.GetString(1),
            Name: reader.GetString(2),
            Description: reader.GetString(3),
            FolderPath: reader.GetString(4),
            SkillMdPath: reader.GetString(5),
            SkillMdMtime: reader.GetInt64(6),
            MetadataJson: reader.IsDBNull(7) ? null : reader.GetString(7),
            Compatibility: reader.IsDBNull(8) ? null : reader.GetString(8),
            License: reader.IsDBNull(9) ? null : reader.GetString(9),
            SuccessCount: reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
            FailureCount: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            LastUsedAt: reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            RegisteredAt: reader.IsDBNull(13) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero));
    }

    static string ComputeId(string absPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(absPath));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}

public sealed record SkillEntry(
    string Id, string? Wing, string Name, string Description,
    string FolderPath, string SkillMdPath, long SkillMdMtime,
    string? MetadataJson, string? Compatibility, string? License,
    int SuccessCount, int FailureCount,
    DateTimeOffset? LastUsedAt, DateTimeOffset RegisteredAt);

public sealed record SkillSearchResult(
    string Id, string? Wing, string Name, string Description,
    string FolderPath, int SuccessCount, int FailureCount,
    float Score, float BoostedScore);
