using Coanda.Core;
using Coanda.Daemon;

namespace Coanda.Tests.Daemon;

public sealed class NodeHomeTests
{
    private static readonly NodeHome Home = new(Path.Combine(Path.GetTempPath(), "coanda-home"));

    [Fact]
    public void A_flights_work_path_sits_under_the_homes_work_directory()
    {
        Guid flightId = Ids.New();

        Home.FlightWorkPath("alpha", flightId).Should().Be(Path.Combine(Home.Path, "work", "alpha", flightId.ToString()));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../..")]
    [InlineData("/tmp/elsewhere")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_project_that_is_not_a_plain_directory_name_has_no_work_path(string project)
    {
        Action work = () => Home.FlightWorkPath(project, Ids.New());

        work.Should().Throw<DomainException>().WithMessage($"Project '{project}' is not a plain directory name*");
    }
}
