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
    public void A_blank_name_is_refused()
    {
        FluentActions.Invoking(() => ProjectDecider.Register(" ", "https://example.invalid/a.git", "main", null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("A project name is required*");
    }
}
