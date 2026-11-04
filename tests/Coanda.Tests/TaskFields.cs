using Coanda.Cli;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Parcels;

namespace Coanda.Tests;

/// <summary>
/// Sets the shipped task's fields that its gates require: an objective and criteria to leave refine,
/// and a pull request to leave build. Every test that moves a task out of refine, or into review,
/// sets them first, through whichever door the test already uses.
/// </summary>
public static class TaskFields
{
    public const string Objective = "objective";
    public const string Criteria = "criteria";
    public const string NonGoals = "non-goals";
    public const string PullRequest = "pull-request";

    public static readonly FieldValue ObjectiveValue = FieldValue.OfText("Give the parcel type named fields.");
    public static readonly FieldValue CriteriaValue = FieldValue.OfItems("A type declares fields.", "A gate reads them.");
    public static readonly FieldValue PullRequestValue = FieldValue.OfReference("42", "https://github.com/example/coanda/pull/42");

    /// <summary>The values to set: the objective and criteria always, and the pull request when asked.</summary>
    public static IEnumerable<(string Field, FieldValue Value)> Brief(bool pullRequest) =>
        pullRequest
            ? [(Objective, ObjectiveValue), (Criteria, CriteriaValue), (PullRequest, PullRequestValue)]
            : [(Objective, ObjectiveValue), (Criteria, CriteriaValue)];

    /// <summary>Applies the values to a parcel through the decider, as a store-free test does.</summary>
    public static Parcel Briefed(this Parcel parcel, ParcelType type, bool pullRequest = true)
    {
        foreach ((string field, FieldValue value) in Brief(pullRequest))
        {
            parcel.Apply(ParcelDecider.SetField(type, parcel, field, value, "brian", DateTimeOffset.UnixEpoch));
        }

        return parcel;
    }

    /// <summary>Sets the values through the parcel service.</summary>
    public static async Task BriefAsync(
        this ParcelService parcels, Principal principal, string project, Guid parcelId, bool pullRequest = true, CancellationToken cancellationToken = default)
    {
        foreach ((string field, FieldValue value) in Brief(pullRequest))
        {
            await parcels.SetFieldAsync(principal, project, parcelId, field, value, cancellationToken);
        }
    }

    /// <summary>Sets the values through the daemon's endpoint, the way <c>coa parcel set</c> does.</summary>
    public static async Task<ParcelView> BriefAsync(
        this DaemonClient client, string project, Guid parcelId, bool pullRequest = true, CancellationToken cancellationToken = default)
    {
        ParcelView? view = null;
        foreach ((string field, FieldValue value) in Brief(pullRequest))
        {
            view = await client.PostAsync<ParcelView>("/parcels/set-field", new ParcelSetFieldRequest(project, parcelId, field, value), cancellationToken);
        }

        return view ?? throw new InvalidOperationException("No field was set.");
    }
}
