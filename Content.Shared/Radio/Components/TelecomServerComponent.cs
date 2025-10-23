namespace Content.Shared.Radio.Components;

/// <summary>
/// Entities with <see cref="TelecomServerComponent"/> are needed to transmit messages using headsets.
/// They also need to be powered by <see cref="ApcPowerReceiverComponent"/>
/// have <see cref="EncryptionKeyHolderComponent"/> and filled with encryption keys
/// of channels in order for them to work on the same map as server.
/// </summary>
[RegisterComponent]
public sealed partial class TelecomServerComponent : Component
{
    /// <summary>
    ///     Absolute maximum range of the server.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("outerRange")]
    public float outerRange = 100;

    /// <summary>
    ///     Range of the server before signal degradation starts.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("innerRange")]
    public float innerRange = 50; //this CANNOT be equal to outer range or there will be a divide by zero error
}
