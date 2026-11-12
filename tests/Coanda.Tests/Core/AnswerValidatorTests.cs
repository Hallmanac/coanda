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
        Assert.Equal(["build", "review", "fix", "discovery"], NameOnlyAnswerSchemas.Names);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void Each_schema_requires_a_string_verdict_with_exactly_its_values(string name, string[] verdicts)
    {
        AnswerSchema schema = Named(name);
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
            Assert.True(AnswerValidator.Validate(Named(name), $$"""{"verdict":"{{verdict}}","summary":"All set."}""").IsValid, verdict);
            Assert.True(AnswerValidator.Validate(Named(name), $$"""{"verdict":"{{verdict}}"}""").IsValid, verdict);
        }
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_verdict_outside_the_set_fails(string name, string[] _)
    {
        AnswerValidation outcome = AnswerValidator.Validate(Named(name), """{"verdict":"maybe"}""");

        Assert.False(outcome.IsValid);
        Assert.NotEmpty(outcome.Failures);
        Assert.False(AnswerValidator.Validate(Named(name), """{"verdict":"Done "}""").IsValid);
        Assert.False(AnswerValidator.Validate(Named(name), """{"verdict":3}""").IsValid);
    }

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void A_document_with_no_verdict_fails(string name, string[] _)
    {
        Assert.False(AnswerValidator.Validate(Named(name), """{"summary":"Nothing decided."}""").IsValid);
        Assert.False(AnswerValidator.Validate(Named(name), "{}").IsValid);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{'verdict':'done'}")]
    [InlineData("""{"verdict":"done"} trailing""")]
    [InlineData("""{"verdict":"done",}""")]
    public void A_document_that_is_not_json_is_reported_invalid_not_thrown(string document)
    {
        AnswerValidation outcome = AnswerValidator.Validate(Named("build"), document);

        Assert.False(outcome.IsValid);
        Assert.Contains("not a JSON document", Assert.Single(outcome.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_is_json_but_not_an_object_fails()
    {
        Assert.False(AnswerValidator.Validate(Named("build"), "\"done\"").IsValid);
        Assert.False(AnswerValidator.Validate(Named("build"), "[]").IsValid);
    }

    [Fact]
    public void Review_findings_must_each_carry_a_title()
    {
        Assert.True(AnswerValidator.Validate(Named("review"), """{"verdict":"not-clean","findings":[{"title":"Race","location":"a.cs:3"}]}""").IsValid);
        Assert.False(AnswerValidator.Validate(Named("review"), """{"verdict":"not-clean","findings":[{"detail":"no title"}]}""").IsValid);
    }

    [Fact]
    public void A_finding_missing_its_title_is_named_with_its_kind_and_full_path()
    {
        AnswerValidation outcome = AnswerValidator.Validate(Named("review"), """{"verdict":"not-clean","findings":[{"detail":"no title"}]}""");

        Assert.Contains(outcome.Failures, f => f.Contains("PropertyRequired") && f.Contains("#/findings[0].title"));
    }

    [Fact]
    public void Each_wrongly_typed_property_inside_a_finding_gets_its_own_line()
    {
        AnswerValidation outcome = AnswerValidator.Validate(
            Named("review"), """{"verdict":"not-clean","findings":[{"title":"ok"},{"title":3,"detail":4}]}""");

        Assert.Contains(outcome.Failures, f => f.Contains("StringExpected") && f.Contains("#/findings[1].title"));
        Assert.Contains(outcome.Failures, f => f.Contains("StringExpected") && f.Contains("#/findings[1].detail"));
    }

    [Fact]
    public void A_top_level_failure_keeps_its_text_alongside_a_nested_one()
    {
        AnswerValidation outcome = AnswerValidator.Validate(Named("review"), """{"verdict":"maybe","findings":[{"detail":"x"}]}""");

        Assert.Contains("NotInEnumeration at #/verdict", outcome.Failures);
        Assert.Contains(outcome.Failures, f => f.Contains("#/findings[0].title"));
    }

    [Fact]
    public void A_schema_refers_within_itself_through_a_json_pointer()
    {
        var schema = new AnswerSchema(
            "pointed",
            ["done"],
            """{"type":"object","required":["verdict"],"definitions":{"verdict":{"type":"string","enum":["done"]}},"properties":{"verdict":{"$ref":"#/definitions/verdict"}}}""");

        Assert.True(AnswerValidator.Validate(schema, """{"verdict":"done"}""").IsValid);
        Assert.False(AnswerValidator.Validate(schema, """{"verdict":"blocked"}""").IsValid);
    }

    [Fact]
    public void A_schema_referring_to_a_file_is_refused_and_the_file_is_not_read()
    {
        string file = Path.Combine(Path.GetTempPath(), $"coanda-answer-{Ids.New():N}.json");
        File.WriteAllText(file, """{"type":"string","enum":["done"]}""");
        try
        {
            var schema = new AnswerSchema(
                "outside", ["done"], """{"type":"object","properties":{"verdict":{"$ref":""" + JsonSerializer.Serialize(file) + "}}}");

            // The file's schema accepts the answer, so a reader that followed the reference would pass it.
            Exception refusal = Assert.ThrowsAny<Exception>(() => AnswerValidator.Validate(schema, """{"verdict":"done"}"""));
            Assert.Contains(file, Messages(refusal), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_schema_referring_to_a_url_is_refused_rather_than_fetched()
    {
        var schema = new AnswerSchema(
            "outside", ["done"], """{"type":"object","properties":{"verdict":{"$ref":"http://127.0.0.1:9/answer.json"}}}""");

        Exception refusal = Assert.ThrowsAny<Exception>(() => AnswerValidator.Validate(schema, """{"verdict":"done"}"""));
        Assert.Contains("refers to 'http://127.0.0.1:9/answer.json', outside itself", Messages(refusal), StringComparison.Ordinal);
    }

    private static AnswerSchema Named(string name) =>
        NameOnlyAnswerSchemas.Find(name) ?? throw new InvalidOperationException($"No name-only schema '{name}'.");

    private static string Messages(Exception exception) => exception.InnerException is { } inner
        ? $"{exception.Message} {Messages(inner)}"
        : exception.Message;
}
