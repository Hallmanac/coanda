using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;

namespace Coanda.Tests.Core;

public sealed class AnswerValidatorTests
{
    public static TheoryData<string, string[]> Verdicts => new()
    {
        { "build", ["done", "blocked"] },
        { "review", ["clean", "not-clean"] },
        { "fix", ["done", "blocked"] },
        { "discovery", ["derive", "carry-forward"] },
    };

    [Fact]
    public void The_catalog_holds_exactly_the_four_shipped_schemas()
    {
        Assert.Equal(["build", "review", "fix", "discovery"], AnswerSchemas.Names);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void Each_schema_requires_a_string_verdict_with_exactly_its_values(string name, string[] verdicts)
    {
        AnswerSchema schema = AnswerSchemas.Require(name);
        Assert.Equal(verdicts, schema.Verdicts);

        using JsonDocument document = JsonDocument.Parse(schema.Document);
        Assert.Contains("verdict", document.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        JsonElement verdict = document.RootElement.GetProperty("properties").GetProperty("verdict");
        Assert.Equal("string", verdict.GetProperty("type").GetString());
        Assert.Equal(verdicts, verdict.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_valid_document_passes_for_each_verdict_value(string name, string[] verdicts)
    {
        foreach (string verdict in verdicts)
        {
            Assert.True(AnswerValidator.Validate(name, $$"""{"verdict":"{{verdict}}","summary":"All set."}""").IsValid, verdict);
            Assert.True(AnswerValidator.Validate(name, $$"""{"verdict":"{{verdict}}"}""").IsValid, verdict);
        }
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_verdict_outside_the_set_fails(string name, string[] _)
    {
        AnswerValidation outcome = AnswerValidator.Validate(name, """{"verdict":"maybe"}""");

        Assert.False(outcome.IsValid);
        Assert.NotEmpty(outcome.Failures);
        Assert.False(AnswerValidator.Validate(name, """{"verdict":"Done "}""").IsValid);
        Assert.False(AnswerValidator.Validate(name, """{"verdict":3}""").IsValid);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_document_with_no_verdict_fails(string name, string[] _)
    {
        Assert.False(AnswerValidator.Validate(name, """{"summary":"Nothing decided."}""").IsValid);
        Assert.False(AnswerValidator.Validate(name, "{}").IsValid);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{'verdict':'done'}")]
    [InlineData("""{"verdict":"done"} trailing""")]
    [InlineData("""{"verdict":"done",}""")]
    public void A_document_that_is_not_json_is_reported_invalid_not_thrown(string document)
    {
        AnswerValidation outcome = AnswerValidator.Validate("build", document);

        Assert.False(outcome.IsValid);
        Assert.Contains("not a JSON document", Assert.Single(outcome.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_is_json_but_not_an_object_fails()
    {
        Assert.False(AnswerValidator.Validate("build", "\"done\"").IsValid);
        Assert.False(AnswerValidator.Validate("build", "[]").IsValid);
    }

    [Fact]
    public void Review_findings_must_each_carry_a_title()
    {
        Assert.True(AnswerValidator.Validate("review", """{"verdict":"not-clean","findings":[{"title":"Race","location":"a.cs:3"}]}""").IsValid);
        Assert.False(AnswerValidator.Validate("review", """{"verdict":"not-clean","findings":[{"detail":"no title"}]}""").IsValid);
    }

    [Fact]
    public void A_finding_missing_its_title_is_named_with_its_kind_and_full_path()
    {
        AnswerValidation outcome = AnswerValidator.Validate("review", """{"verdict":"not-clean","findings":[{"detail":"no title"}]}""");

        Assert.Contains(outcome.Failures, f => f.Contains("PropertyRequired") && f.Contains("#/findings[0].title"));
    }

    [Fact]
    public void Each_wrongly_typed_property_inside_a_finding_gets_its_own_line()
    {
        AnswerValidation outcome = AnswerValidator.Validate(
            "review", """{"verdict":"not-clean","findings":[{"title":"ok"},{"title":3,"detail":4}]}""");

        Assert.Contains(outcome.Failures, f => f.Contains("StringExpected") && f.Contains("#/findings[1].title"));
        Assert.Contains(outcome.Failures, f => f.Contains("StringExpected") && f.Contains("#/findings[1].detail"));
    }

    [Fact]
    public void A_top_level_failure_keeps_its_text_alongside_a_nested_one()
    {
        AnswerValidation outcome = AnswerValidator.Validate("review", """{"verdict":"maybe","findings":[{"detail":"x"}]}""");

        Assert.Contains("NotInEnumeration at #/verdict", outcome.Failures);
        Assert.Contains(outcome.Failures, f => f.Contains("#/findings[0].title"));
    }

    [Fact]
    public void An_unknown_schema_name_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => AnswerValidator.Validate("essay", """{"verdict":"done"}"""));
        Assert.StartsWith("'essay' is not a shipped answer schema", exception.Message, StringComparison.Ordinal);
    }
}
