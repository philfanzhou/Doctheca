namespace Doctheca.Tests.Parsing;

/// <summary>One stored-input corpus for both the runtime parser and guarded SQL migration.</summary>
public static class HeadingLevelCases
{
    public static IEnumerable<object?[]> All =>
    [
        ["title", null, -2], ["title", "", -2], ["title", "heading-", -2],
        ["title", "heading-1", 1], ["title", "HeAdInG-02", 2],
        ["title", "heading-+2", 2], ["title", "heading- \t\n\v\f\r+0003 \r\n\t\v\f", 3],
        ["title", "heading-2147483647", int.MaxValue],
        ["title", "heading-000000000000000000000000000000000000000001", 1],
        ["title", "heading-0", -2], ["title", "heading-+0", -2],
        ["title", "heading--1", -2], ["title", "heading--2147483648", -2],
        ["title", "heading-2147483648", -2],
        ["title", "heading-999999999999999999999999999999999999999999", -2],
        ["title", "heading-1.0", -2], ["title", "heading-1e0", -2], ["title", "heading-1 2", -2],
        ["title", "heading-++1", -2], ["title", "heading-+ 1", -2],
        ["title", "heading-１", -2], ["title", "heading-١", -2],
        ["title", "heading-\u00a01", -2], ["title", "heading-1\u00a0", -2],
        ["title", "heading-\u00851", -2], ["title", "headıng-1", -2],
        ["title", " heading-1", -2], ["title", "not-heading-1", -2],
        ["text", null, -1], ["text", "heading-+3", 3],
        ["image", "heading--1", -1], ["image", "heading-4", 4],
        ["TITLE", null, -1], ["Title", "malformed", -1], ["title-other", null, -1],
        [null, null, -1], ["text", "heading-0", -1]
    ];
}
