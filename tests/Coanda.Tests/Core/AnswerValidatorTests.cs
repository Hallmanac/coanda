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
        AnswerSchemas.Names.Should().Equal("build", "review", "fix", "discovery");
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void Each_schema_requires_a_string_verdict_with_exactly_its_values(string name, string[] verdicts)
    {
        AnswerSchema schema = AnswerSchemas.Require(name);
        schema.Verdicts.Should().Equal(verdicts);

        using JsonDocument document = JsonDocument.Parse(schema.Document);
        document.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Contain("verdict");
        JsonElement verdict = document.RootElement.GetProperty("properties").GetProperty("verdict");
        verdict.GetProperty("type").GetString().Should().Be("string");
        verdict.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(verdicts);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_valid_document_passes_for_each_verdict_value(string name, string[] verdicts)
    {
        foreach (string verdict in verdicts)
        {
            AnswerValidator.Validate(name, $$"""{"verdict":"{{verdict}}","summary":"All set."}""").IsValid.Should().BeTrue(verdict);
            AnswerValidator.Validate(name, $$"""{"verdict":"{{verdict}}"}""").IsValid.Should().BeTrue(verdict);
        }
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_verdict_outside_the_set_fails(string name, string[] _)
    {
        AnswerValidation outcome = AnswerValidator.Validate(name, """{"verdict":"maybe"}""");

        outcome.IsValid.Should().BeFalse();
        outcome.Failures.Should().NotBeEmpty();
        AnswerValidator.Validate(name, """{"verdict":"Done "}""").IsValid.Should().BeFalse();
        AnswerValidator.Validate(name, """{"verdict":3}""").IsValid.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_document_with_no_verdict_fails(string name, string[] _)
    {
        AnswerValidator.Validate(name, """{"summary":"Nothing decided."}""").IsValid.Should().BeFalse();
        AnswerValidator.Validate(name, "{}").IsValid.Should().BeFalse();
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

        outcome.IsValid.Should().BeFalse();
        outcome.Failures.Should().ContainSingle().Which.Should().Contain("not a JSON document");
    }

    [Fact]
    public void A_document_that_is_json_but_not_an_object_fails()
    {
        AnswerValidator.Validate("build", "\"done\"").IsValid.Should().BeFalse();
        AnswerValidator.Validate("build", "[]").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Review_findings_must_each_carry_a_title()
    {
        AnswerValidator.Validate("review", """{"verdict":"not-clean","findings":[{"title":"Race","location":"a.cs:3"}]}""").IsValid.Should().BeTrue();
        AnswerValidator.Validate("review", """{"verdict":"not-clean","findings":[{"detail":"no title"}]}""").IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_schema_name_is_refused()
    {
        FluentActions.Invoking(() => AnswerValidator.Validate("essay", """{"verdict":"done"}"""))
            .Should().Throw<DomainException>().WithMessage("'essay' is not a shipped answer schema*");
    }
}
