using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>
/// Who may set a field, as the parcel decider rules it: a field a module owns is set by that module,
/// with or without a dispatch, and by an Owner-role principal, and every other caller is refused it; a
/// field no module owns is set as before.
/// </summary>
public sealed class OwnedFieldDecisionTests
{
    private const string Owned = "objective";
    private const string Unowned = "criteria";

    private static readonly ParcelType Type = ShippedTypes.Task with
    {
        Fields = [.. ShippedTypes.Task.Fields.Select(f => f.Name == Owned ? f with { Owner = "software" } : f)],
    };

    public static TheoryData<string, Principal, bool> Allowed => new()
    {
        { "the owning module with no dispatch", Principal.ForModule("software"), false },
        { "the owning module under a dispatch", Principal.ForModule("software"), true },
        { "the owner", Principal.LocalOwner, false },
        { "another Owner-role principal", new Principal("coanda-seed", Role.Owner), false },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void An_owned_field_is_set_by_its_module_and_by_the_owner(string who, Principal principal, bool underDispatch)
    {
        Assert.False(string.IsNullOrEmpty(who));

        ParcelDecider.RequireMaySetField(Type, Owned, principal, underDispatch);
    }

    public static TheoryData<string, Principal, bool, string> Refused => new()
    {
        { "a member", new Principal("ana", Role.Member), false, "the Member principal 'ana'" },
        { "an agent step's session", Principal.ForSession(new SessionBinding("alpha", Guid.Empty, Guid.Empty)), false, $"the Agent principal 'session:{Guid.Empty}'" },
        { "the daemon's runner", new Principal("coanda-runner", Role.Agent), false, "the Agent principal 'coanda-runner'" },
        { "another module", Principal.ForModule("jira"), false, "module 'jira'" },
        { "another module under a dispatch", Principal.ForModule("jira"), true, "module 'jira'" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void An_owned_field_is_refused_every_other_caller(string who, Principal principal, bool underDispatch, string described)
    {
        Assert.False(string.IsNullOrEmpty(who));

        DomainException refused = Assert.Throws<DomainException>(() => ParcelDecider.RequireMaySetField(Type, Owned, principal, underDispatch));

        Assert.Equal(
            $"Field '{Owned}' of a 'task' is owned by module 'software'; it is set by that module and by an Owner-role principal, and {described} is neither.",
            refused.Message);
    }

    [Fact]
    public void A_field_no_module_owns_is_set_as_before()
    {
        ParcelDecider.RequireMaySetField(Type, Unowned, new Principal("ana", Role.Member), underDispatch: false);
        ParcelDecider.RequireMaySetField(Type, Unowned, new Principal("coanda-runner", Role.Agent), underDispatch: false);
        ParcelDecider.RequireMaySetField(Type, Unowned, Principal.ForModule("jira"), underDispatch: true);

        DomainException refused = Assert.Throws<DomainException>(
            () => ParcelDecider.RequireMaySetField(Type, Unowned, Principal.ForModule("software"), underDispatch: false));
        Assert.Equal(
            "Module 'software' sets a parcel's field only by naming a dispatch of one of its own steps that it holds; this set-field named none.",
            refused.Message);
    }
}
