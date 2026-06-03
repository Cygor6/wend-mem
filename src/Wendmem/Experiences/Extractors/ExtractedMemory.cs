namespace Wendmem.Experiences.Extractors;

public record ExtractedMemory(
    string WhenToUse,
    string Content,
    string[] Keywords,
    float Score,
    string[] ToolsUsed,
    TaskMemorySource Source);
