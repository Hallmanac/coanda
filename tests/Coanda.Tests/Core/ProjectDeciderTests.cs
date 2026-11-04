using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;

namespace Coanda.Tests.Core;

public sealed class ProjectDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registering_records_the_trimmed_name_and_the_remote_and_branch_exactly_as_given()
    {
        ProjectRegistered registered = ProjectDecider.Register(" coanda ", "git@github.com:you/coanda.git ", " main", null, "brian", Now);

        Assert.Equal("coanda", registered.Name);
        Assert.Equal("git@github.com:you/coanda.git ", registered.Remote);
        Assert.Equal(" main", registered.PrimaryBranch);
        Assert.Equal("brian", registered.RegisteredBy);
        Assert.Equal(Now, registered.RegisteredAt);

        Project project = Project.Create(registered);
        Assert.Equal(registered.ProjectId, project.Id);
        Assert.Equal("coanda", project.Name);
        Assert.Equal(registered.Remote, project.Remote);
        Assert.Equal(registered.PrimaryBranch, project.PrimaryBranch);
    }

    [Fact]
    public void A_name_that_already_has_a_record_is_refused()
    {
        Project existing = Project.Create(ProjectDecider.Register("coanda", "https://example.invalid/a.git", "main", null, "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register("coanda", "https://example.invalid/b.git", "trunk", existing, "brian", Now));
        Assert.Matches(@"(?s)^Project 'coanda' is already registered.*https://example\.invalid/a\.git.*main.*$", exception.Message);
    }

    [Fact]
    public void A_name_differing_from_a_registered_one_only_in_letter_case_is_refused_naming_it()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register(" Coanda ", "https://example.invalid/b.git", "main", null, "brian", Now, registeredNames: ["alpha", "coanda"]));
        Assert.StartsWith("Project 'Coanda' differs from the registered project 'coanda' only in letter case", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_no_registered_one_matches_even_ignoring_case_is_registered()
    {
        Assert.Equal("coanda-two", ProjectDecider.Register("coanda-two", "https://example.invalid/b.git", "main", null, "brian", Now, registeredNames: ["coanda", "Alpha"])
            .Name);
    }

    [Theory]
    [InlineData("", "main", "needs the remote of its repository")]
    [InlineData("   ", "main", "needs the remote of its repository")]
    [InlineData("https://example.invalid/a.git", "", "needs the primary branch of its repository")]
    [InlineData("https://example.invalid/a.git", "  ", "needs the primary branch of its repository")]
    public void A_blank_remote_or_branch_is_refused(string remote, string branch, string rule)
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register("coanda", remote, branch, null, "brian", Now));
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_build_and_test_commands_are_recorded_as_given_and_default_to_dotnet_when_none_is_named()
    {
        Project named = Project.Create(ProjectDecider.Register("a", "https://example.invalid/a.git", "main", null, "brian", Now, "make", " make test "));
        Project unnamed = Project.Create(ProjectDecider.Register("b", "https://example.invalid/b.git", "main", null, "brian", Now));

        Assert.Equal(("make", "make", " make test ", " make test "), (named.BuildCommand, named.Build, named.TestCommand, named.Test));
        Assert.Equal((null, "dotnet build", null, "dotnet test"), (unnamed.BuildCommand, unnamed.Build, unnamed.TestCommand, unnamed.Test));
    }

    [Theory]
    [InlineData("", null, "blank build command")]
    [InlineData("  ", null, "blank build command")]
    [InlineData(null, "", "blank test command")]
    [InlineData(null, " ", "blank test command")]
    public void A_blank_build_or_test_command_is_refused(string? build, string? test, string rule)
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register("coanda", "https://example.invalid/a.git", "main", null, "brian", Now, build, test));
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registration_recorded_before_the_build_and_test_commands_existed_reads_back_with_the_defaults()
    {
        const string Recorded = """{"ProjectId":"01a1a29e-778a-7730-9f07-304d9ac0ffeb","Name":"old","Remote":"r","PrimaryBranch":"main","RegisteredBy":"brian","RegisteredAt":"2026-11-03T12:00:00+00:00"}""";

        ProjectRegistered registered = System.Text.Json.JsonSerializer.Deserialize<ProjectRegistered>(Recorded)
            ?? throw new InvalidOperationException("The recorded event did not read back.");

        Assert.Equal(Project.DefaultBuild, Project.Create(registered).Build);
        Assert.Null(registered.TestCommand);
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register(" ", "https://example.invalid/a.git", "main", null, "brian", Now));
        Assert.StartsWith("A project name is required", exception.Message, StringComparison.Ordinal);
    }
}
