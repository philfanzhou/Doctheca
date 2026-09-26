namespace Doctheca.Service;

/// <summary>
/// Builds prompts for LLM-based document metadata analysis.
/// </summary>
internal static class DocumentAnalysisPromptBuilder
{
    /// <summary>
    /// Build a focused prompt for metadata-only analysis (subject, grade, year).
    /// Internal for unit testing.
    /// </summary>
    internal static string BuildMetadataAnalysisPrompt(string textPreview)
    {
        return $$"""
            你是一个文档分析专家。分析以下文档内容，识别学科和年级。

            学科类型：
            - English：英语教材、阅读材料
            - 语文：语文教材、文言文、现代文
            - 数学：数学教材、习题集
            - 物理：物理教材、实验报告
            - 化学：化学教材、实验报告
            - 生物：生物教材
            - 其他：无法明确判断

            年级（从标题或内容推断）：
            - K：幼儿园/学前
            - G1-G12：小学一年级到高三
            - 无法判断时返回 null

            年份（从标题、页眉、版权页等推断，4位数字）：
            - 无法判断时返回 null

            请分析以下文档内容并返回 JSON。只返回 JSON，不要有其他文字。

            ```json
            {
              "subject": "学科或null",
              "grade": "年级或null",
              "year": "年份或null"
            }
            ```

            文档内容：
            ---
            {{textPreview}}
            ---
            """;
    }
}
