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

        registered.Name.Should().Be("coanda");
        registered.Remote.Should().Be("git@github.com:you/coanda.git ");
        registered.PrimaryBranch.Should().Be(" main");
        registered.RegisteredBy.Should().Be("brian");
        registered.RegisteredAt.Should().Be(Now);

        Project project = Project.Create(registered);
        project.Id.Should().Be(registered.ProjectId);
        project.Name.Should().Be("coanda");
        project.Remote.Should().Be(registered.Remote);
        project.PrimaryBranch.Should().Be(registered.PrimaryBranch);
    }

    [Fact]
    public void A_name_that_already_has_a_record_is_refused()
    {
        Project existing = Project.Create(ProjectDecider.Register("coanda", "https://example.invalid/a.git", "main", null, "brian", Now));

        FluentActions.Invoking(() => ProjectDecider.Register("coanda", "https://example.invalid/b.git", "trunk", existing, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("Project 'coanda' is already registered*https://example.invalid/a.git*main*");
    }

    [Fact]
    public void A_name_differing_from_a_registered_one_only_in_letter_case_is_refused_naming_it()
    {
        FluentActions.Invoking(() => ProjectDecider.Register(" Coanda ", "https://example.invalid/b.git", "main", null, "brian", Now, registeredNames: ["alpha", "coanda"]))
            .Should().Throw<DomainException>().WithMessage("Project 'Coanda' differs from the registered project 'coanda' only in letter case*");
    }

    [Fact]
    public void A_name_no_registered_one_matches_even_ignoring_case_is_registered()
    {
        ProjectDecider.Register("coanda-two", "https://example.invalid/b.git", "main", null, "brian", Now, registeredNames: ["coanda", "Alpha"])
            .Name.Should().Be("coanda-two");
    }

    [Theory]
    [InlineData("", "main", "*needs the remote of its repository*")]
    [InlineData("   ", "main", "*needs the remote of its repository*")]
    [InlineData("https://example.invalid/a.git", "", "*needs the primary branch of its repository*")]
    [InlineData("https://example.invalid/a.git", "  ", "*needs the primary branch of its repository*")]
    public void A_blank_remote_or_branch_is_refused(string remote, string branch, string rule)
    {
        FluentActions.Invoking(() => ProjectDecider.Register("coanda", remote, branch, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage(rule);
    }

    [Fact]
    public void The_build_and_test_commands_are_recorded_as_given_and_default_to_dotnet_when_none_is_named()
    {
        Project named = Project.Create(ProjectDecider.Register("a", "https://example.invalid/a.git", "main", null, "brian", Now, "make", " make test "));
        Project unnamed = Project.Create(ProjectDecider.Register("b", "https://example.invalid/b.git", "main", null, "brian", Now));

        (named.BuildCommand, named.Build, named.TestCommand, named.Test).Should().Be(("make", "make", " make test ", " make test "));
        (unnamed.BuildCommand, unnamed.Build, unnamed.TestCommand, unnamed.Test).Should().Be((null, "dotnet build", null, "dotnet test"));
    }

    [Theory]
    [InlineData("", null, "*blank build command*")]
    [InlineData("  ", null, "*blank build command*")]
    [InlineData(null, "", "*blank test command*")]
    [InlineData(null, " ", "*blank test command*")]
    public void A_blank_build_or_test_command_is_refused(string? build, string? test, string rule)
    {
        FluentActions.Invoking(() => ProjectDecider.Register("coanda", "https://example.invalid/a.git", "main", null, "brian", Now, build, test))
            .Should().Throw<DomainException>().WithMessage(rule);
    }

    [Fact]
    public void A_registration_recorded_before_the_build_and_test_commands_existed_reads_back_with_the_defaults()
    {
        const string Recorded = """{"ProjectId":"01a1a29e-778a-7730-9f07-304d9ac0ffeb","Name":"old","Remote":"r","PrimaryBranch":"main","RegisteredBy":"brian","RegisteredAt":"2026-11-03T12:00:00+00:00"}""";

        ProjectRegistered registered = System.Text.Json.JsonSerializer.Deserialize<ProjectRegistered>(Recorded)
            ?? throw new InvalidOperationException("The recorded event did not read back.");

        Project.Create(registered).Build.Should().Be(Project.DefaultBuild);
        registered.TestCommand.Should().BeNull();
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        FluentActions.Invoking(() => ProjectDecider.Register(" ", "https://example.invalid/a.git", "main", null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("A project name is required*");
    }
}
