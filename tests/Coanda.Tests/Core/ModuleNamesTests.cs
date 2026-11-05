using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Tests.Core;

public sealed class ModuleNamesTests
{
    [Theory]
    [InlineData("software")]
    [InlineData("mail_2")]
    [InlineData("jira-sync")]
    public void A_plain_name_names_a_module_and_its_principal(string name)
    {
        Principal principal = Principal.ForModule(name);

        Assert.Equal(new Principal($"module:{name}", Role.Module, Module: name), principal);
        Assert.Null(principal.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("software/build")]
    [InlineData("two words")]
    [InlineData("dot.ted")]
    public void Any_other_name_is_refused(string name)
    {
        DomainException refused = Assert.Throws<DomainException>(() => Principal.ForModule(name));

        Assert.Equal($"A module's name is a plain name (letters, digits, hyphens, and underscores); '{name}' is not.", refused.Message);
    }

    [Theory]
    [InlineData("software/open-pr", "software")]
    [InlineData("test/answers-running", "test")]
    [InlineData("a_1/b-2", "a_1")]
    public void A_prefixed_name_gives_its_module(string name, string module) =>
        Assert.Equal(module, ModuleNames.PrefixOf(name));

    [Theory]
    [InlineData("open-pr")]
    [InlineData("/open-pr")]
    [InlineData("software/")]
    [InlineData("software/open/pr")]
    [InlineData("soft ware/open-pr")]
    [InlineData("software/open pr")]
    [InlineData("")]
    public void A_name_not_of_the_one_prefixed_form_gives_none(string name)
    {
        Assert.Null(ModuleNames.PrefixOf(name));
        Assert.False(ModuleNames.IsPrefixed(name));
    }
}
