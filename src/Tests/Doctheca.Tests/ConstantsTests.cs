using System;
using global::Doctheca.Domain.Models;
using Xunit;

namespace Doctheca.Tests;

public class ConstantsTests
{
    #region Subject Validation

    [Theory]
    [InlineData("英语", true)]
    [InlineData("语文", true)]
    [InlineData("数学", true)]
    [InlineData("物理", true)]
    [InlineData("化学", true)]
    [InlineData("生物", true)]
    [InlineData("其他", true)]
    [InlineData("历史", false)]
    [InlineData("", false)]
    public void IsValidSubject_ValidatesCorrectly(string subject, bool expected)
    {
        Assert.Equal(expected, DocthecaConstants.IsValidSubject(subject));
    }

    [Fact]
    public void ValidSubjects_ContainsAll7Subjects()
    {
        Assert.Equal(7, DocthecaConstants.ValidSubjects.Length);
        Assert.Contains("英语", DocthecaConstants.ValidSubjects);
        Assert.Contains("语文", DocthecaConstants.ValidSubjects);
        Assert.Contains("数学", DocthecaConstants.ValidSubjects);
        Assert.Contains("物理", DocthecaConstants.ValidSubjects);
        Assert.Contains("化学", DocthecaConstants.ValidSubjects);
        Assert.Contains("生物", DocthecaConstants.ValidSubjects);
        Assert.Contains("其他", DocthecaConstants.ValidSubjects);
    }

    #endregion

    #region Grade Validation

    [Theory]
    [InlineData("K", true)]
    [InlineData("G1", true)]
    [InlineData("G6", true)]
    [InlineData("G7", true)]
    [InlineData("G9", true)]
    [InlineData("G10", true)]
    [InlineData("G12", true)]
    [InlineData("G13", false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("高一", false)] // Labels are for display, not validation
    public void IsValidGrade_ValidatesCorrectly(string grade, bool expected)
    {
        Assert.Equal(expected, DocthecaConstants.IsValidGrade(grade));
    }

    [Fact]
    public void ValidGrades_Contains13Levels()
    {
        Assert.Equal(13, DocthecaConstants.ValidGrades.Length);
        Assert.Contains("K", DocthecaConstants.ValidGrades);
        Assert.Contains("G1", DocthecaConstants.ValidGrades);
        Assert.Contains("G12", DocthecaConstants.ValidGrades);
    }

    [Fact]
    public void GradeLabels_HasLabelsForAllGrades()
    {
        Assert.Equal(13, DocthecaConstants.GradeLabels.Count);
        Assert.Equal("Kindergarten", DocthecaConstants.GradeLabels["K"]);
        Assert.Equal("Grade 10", DocthecaConstants.GradeLabels["G10"]);
        Assert.Equal("Grade 12", DocthecaConstants.GradeLabels["G12"]);
    }

    #endregion

    #region Subject Constant

    [Fact]
    public void SubjectEnglish_IsEnglish()
    {
        Assert.Equal("英语", DocthecaConstants.SubjectEnglish);
    }

    #endregion

    #region SearchConfig Defaults

    [Fact]
    public void OpenSearchOptions_HasCorrectDefaults()
    {
        var options = new OpenSearchOptions();
        Assert.Equal("http://localhost:9200", options.Url);
        Assert.Equal("doctheca-segments", options.IndexName);
    }

    #endregion

    #region SearchFilterModel

    [Fact]
    public void SearchFilterModel_DefaultValues_AreNull()
    {
        var filter = new SearchFilterModel();
        Assert.Null(filter.DocumentTitle);
        Assert.Null(filter.Subject);
        Assert.Null(filter.Grade);
        Assert.Null(filter.Year);
    }

    #endregion

    #region SearchResultModel

    [Fact]
    public void SearchResultModel_DefaultValues()
    {
        var result = new SearchResultModel();
        Assert.Equal(string.Empty, result.DocumentName);
        Assert.Equal(0, result.PageNumber);
        Assert.Equal(string.Empty, result.AssociatedText);
        Assert.Equal(0, result.Score);
        Assert.Equal(string.Empty, result.MatchType);
        Assert.Equal(string.Empty, result.SegmentId);
        Assert.Equal(0, result.StartOffset);
        Assert.Equal(0, result.EndOffset);
    }

    #endregion

    #region SourceTypes Constants

    [Fact]
    public void SourceTypes_HasAllSourceTypes()
    {
        Assert.Equal("pdf", SourceTypes.Pdf);
        Assert.Equal("word", SourceTypes.Word);
        Assert.Equal("ppt", SourceTypes.Ppt);
        Assert.Equal("unknown", SourceTypes.Unknown);
    }

    #endregion

    #region SearchMatchType Constants

    [Fact]
    public void SearchMatchType_HasAllMatchTypes()
    {
        Assert.Equal("exact_phrase", SearchMatchType.ExactPhrase);
        Assert.Equal("stemmed", SearchMatchType.Stemmed);
        Assert.Equal("exact_word", SearchMatchType.ExactWord);
    }

    #endregion
}
