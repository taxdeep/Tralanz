namespace Citus.Modules.UnityAi.Domain.Shared;

/// <summary>
/// Capability classes a provider may declare support for. Used by the model
/// router to decide whether a provider can serve a given task type.
/// </summary>
public static class UnityAiCapability
{
    public const string CheapClassification = "cheap_classification";
    public const string Summarization = "summarization";
    public const string StructuredOutput = "structured_output";
    public const string TextReasoning = "text_reasoning";
    public const string VisionOcr = "vision_ocr";
    public const string Embedding = "embedding";
    public const string Reranking = "reranking";
    public const string ToolCalling = "tool_calling";
}
