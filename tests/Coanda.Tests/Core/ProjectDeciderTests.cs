using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;

namespace Coanda.Tests.Core;

public sealed class ProjectDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registering_records_the_trimmed_name_and_one_repo_at_the_remote_exactly_as_given()
    {
        ProjectRegistered registered = ProjectDecider.Register(" coanda ", "git@github.com:you/Coanda.git ", null, "brian", Now);

        Assert.Equal("coanda", registered.Name);
        Assert.Equal([new ProjectRepo("coanda", "git@github.com:you/Coanda.git ")], registered.Repos);
        Assert.Null(registered.Remote);
        Assert.Equal("brian", registered.RegisteredBy);
        Assert.Equal(Now, registered.RegisteredAt);

        Project project = Project.Create(registered);
        Assert.Equal(registered.ProjectId, project.Id);
        Assert.Equal("coanda", project.Name);
        Assert.Equal(registered.Repos, project.Repos);
    }

    [Fact]
    public void A_name_that_already_has_a_record_is_refused_naming_its_repos()
    {
        Project existing = Project.Create(ProjectDecider.Register("coanda", "https://example.invalid/a.git", null, "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register("coanda", "https://example.invalid/b.git", existing, "brian", Now));
        Assert.Equal("Project 'coanda' is already registered, with the repos a (https://example.invalid/a.git).", exception.Message);
    }

    [Fact]
    public void A_name_differing_from_a_registered_one_only_in_letter_case_is_refused_naming_it()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register(" Coanda ", "https://example.invalid/b.git", null, "brian", Now, registeredNames: ["alpha", "coanda"]));
        Assert.StartsWith("Project 'Coanda' differs from the registered project 'coanda' only in letter case", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_no_registered_one_matches_even_ignoring_case_is_registered()
    {
        Assert.Equal("coanda-two", ProjectDecider.Register("coanda-two", "https://example.invalid/b.git", null, "brian", Now, registeredNames: ["coanda", "Alpha"])
            .Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_remote_is_refused(string remote)
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register("coanda", remote, null, "brian", Now));
        Assert.Equal("Project 'coanda' needs the remote of a repo; none was given.", exception.Message);
    }

    [Fact]
    public void A_registration_an_earlier_build_recorded_reads_as_one_repo_at_its_remote_and_drops_the_software_settings()
    {
        const string Recorded = """{"projectId":"01a1a29e-778a-7730-9f07-304d9ac0ffeb","name":"old","remote":"https://github.com/example/Old-Repo.git","primaryBranch":"main","registeredBy":"brian","registeredAt":"2026-11-03T12:00:00+00:00","buildCommand":"make"}""";

        ProjectRegistered registered = System.Text.Json.JsonSerializer.Deserialize<ProjectRegistered>(Recorded, System.Text.Json.JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The recorded event did not read back.");

        Assert.Null(registered.Repos);
        Assert.Equal([new ProjectRepo("old-repo", "https://github.com/example/Old-Repo.git")], Project.Create(registered).Repos);
    }

    [Fact]
    public void A_registration_this_build_writes_names_no_remote_of_its_own()
    {
        ProjectRegistered registered = ProjectDecider.Register("coanda", "https://example.invalid/a.git", null, "brian", Now);

        string written = System.Text.Json.JsonSerializer.Serialize(registered, System.Text.Json.JsonSerializerOptions.Web);

        Assert.DoesNotContain("\"remote\":null", written, StringComparison.Ordinal);
        Assert.Contains("\"repos\":[{\"name\":\"a\",\"remote\":\"https://example.invalid/a.git\"}]", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_name_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ProjectDecider.Register(" ", "https://example.invalid/a.git", null, "brian", Now));
        Assert.StartsWith("A project name is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_modules_on_records_each_once_in_ordinal_order_and_replaces_the_set()
    {
        Project project = Project.Create(ProjectDecider.Register("coanda", "https://example.invalid/a.git", null, "brian", Now));

        ProjectModulesSet set = ProjectDecider.SetModules("coanda", project, ["software", "mail", "software"], "brian", Now);
        project.Apply(set);

        Assert.Equal(project.Id, set.ProjectId);
        Assert.Equal("coanda", set.Project);
        Assert.Equal(["mail", "software"], set.Modules);
        Assert.Equal("brian", set.SetBy);
        Assert.Equal(Now, set.SetAt);
        Assert.Equal(["mail", "software"], project.Modules);
        Assert.True(project.IsSwitchedOn("software"));
        Assert.False(project.IsSwitchedOn("Software"));

        project.Apply(ProjectDecider.SetModules("coanda", project, [], "brian", Now));
        Assert.Empty(project.Modules);
        Assert.False(project.IsSwitchedOn("software"));
    }

    [Fact]
    public void Switching_on_a_module_that_is_not_a_module_name_or_in_an_unregistered_project_is_refused()
    {
        Project project = Project.Create(ProjectDecider.Register("coanda", "https://example.invalid/a.git", null, "brian", Now));

        DomainException notPlain = Assert.Throws<DomainException>(() => ProjectDecider.SetModules("coanda", project, ["software/build"], "brian", Now));
        DomainException unregistered = Assert.Throws<DomainException>(() => ProjectDecider.SetModules("other", null, ["software"], "brian", Now));

        Assert.Equal(
            "Project 'coanda' was given 'software/build' as a module to switch on; a module's name is lowercase letters and digits in runs joined by single hyphens.",
            notPlain.Message);
        Assert.Equal(ProjectDecider.Unregistered("other"), unregistered.Message);
    }
}
