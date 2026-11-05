namespace Coanda.Sdk.Contract;

/// <summary>A core event kind a module's describe can subscribe to and the feed delivers.</summary>
public enum CoreEventKind
{
    /// <summary>A parcel entered a stage, by being created in it or moved into it.</summary>
    ParcelEntered,

    /// <summary>A flow run reached the end of its flow.</summary>
    FlowRunEnded,

    /// <summary>Someone stopped a live flow run before its end.</summary>
    FlowRunStopped,

    /// <summary>A flow run stopped to wait for a person.</summary>
    FlowRunPaused,

    /// <summary>A value was recorded in one of a parcel's fields.</summary>
    ParcelFieldSet,
}
