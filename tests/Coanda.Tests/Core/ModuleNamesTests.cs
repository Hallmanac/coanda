using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Tests.Core;

public sealed class ModuleNamesTests
{
    [Theory]
    [InlineData("software")]
    [InlineData("jira-sync")]
    [InlineData("mail2")]
    [InlineData("a-1-b")]
    public void A_module_name_names_a_module_and_its_principal(string name)
    {
        Principal principal = Principal.ForModule(name);

        Assert.True(ModuleNames.IsModuleName(name));
        Assert.Equal(name, ModuleNames.Require(name));
        Assert.Equal(new Principal($"module:{name}", Role.Module, Module: name), principal);
        Assert.Null(principal.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mail")]
    [InlineData("mail_2")]
    [InlineData("-mail")]
    [InlineData("mail-")]
    [InlineData("mail--sync")]
    [InlineData("mail\n")]
    [InlineData("software/build")]
    [InlineData("two words")]
    [InlineData("dot.ted")]
    public void Any_other_name_is_refused(string name)
    {
        DomainException refused = Assert.Throws<DomainException>(() => Principal.ForModule(name));

        Assert.False(ModuleNames.IsModuleName(name));
        Assert.Equal(
            $"A module's name is lowercase letters and digits in runs joined by single hyphens; '{name}' is not.",
            refused.Message);
        Assert.Equal(refused.Message, Assert.Throws<DomainException>(() => ModuleNames.Require(name)).Message);
    }

    [Fact]
    public void No_name_is_a_module_name() => Assert.False(ModuleNames.IsModuleName(null));

    [Theory]
    [InlineData("software/open-pr", "software")]
    [InlineData("test/answers-running", "test")]
    [InlineData("jira-sync/issue", "jira-sync")]
    [InlineData("a1/b_2", "a1")]
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
    [InlineData("Mail/issue")]
    [InlineData("mail_2/issue")]
    [InlineData("-mail/issue")]
    [InlineData("mail-/issue")]
    [InlineData("mail--sync/issue")]
    [InlineData("mail\n/issue")]
    public void A_name_not_of_the_one_prefixed_form_gives_none(string name)
    {
        Assert.Null(ModuleNames.PrefixOf(name));
        Assert.False(ModuleNames.IsPrefixed(name));
    }
}
