using Content.Client.Decals.UI;
using Content.Client.Gameplay;
using Content.Client.Sandbox;
using Content.Shared.Decals;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.UserInterface.Systems.DecalPlacer;

public sealed class DecalPlacerUIController : UIController, IOnStateExited<GameplayState>, IOnSystemChanged<SandboxSystem>
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IEntityManager _e = default!;
    [UISystemDependency] private readonly SandboxSystem _sandbox = default!;
    private readonly DecalPlacementSystem _decalPlacementSystem = _e.System<DecalPlacementSystem>();
    private DecalPlacerWindow? _window;

    public void ToggleWindow()
    {
        EnsureWindow();

        if (_window!.IsOpen)
        {
            _window.Close();
        }
        else if (_sandbox.SandboxAllowed)
        {
            _window.Open();
        }
    }

    public void OnStateEntered()
    {
        CommandBinds.Builder
            .Bind(EngineKeyFunctions.EditorCancelPlace, new PointerInputCmdHandler(OpenDecalViewer))
            .Register<DecalPlacementSystem>();
    }

    public void OnStateExited(GameplayState state)
    {
        if (_window == null)
            return;
        _window.Dispose();
        _window = null;

        CommandBinds.Unregister<DecalPlacementSystem>();
    }

    public void OnSystemLoaded(SandboxSystem system)
    {
        _sandbox.SandboxDisabled += CloseWindow;
        _prototypes.PrototypesReloaded += OnPrototypesReloaded;
    }

    public void OnSystemUnloaded(SandboxSystem system)
    {
        _sandbox.SandboxDisabled -= CloseWindow;
        _prototypes.PrototypesReloaded -= OnPrototypesReloaded;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs obj)
    {
        if (obj.WasModified<DecalPrototype>())
            ReloadPrototypes();
    }

    private void ReloadPrototypes()
    {
        if (_window == null || _window.Disposed)
            return;

        var prototypes = _prototypes.EnumeratePrototypes<DecalPrototype>();
        _window.Populate(prototypes);
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;
        _window = UIManager.CreateWindow<DecalPlacerWindow>();
        LayoutContainer.SetAnchorPreset(_window, LayoutContainer.LayoutPreset.CenterLeft);
        ReloadPrototypes();
    }

    private void CloseWindow()
    {
        if (_window == null || _window.Disposed)
            return;

        _window.Close();
    }

    private void OpenDecalViewer(ICommonSession? session, EntityCoordinates coords, EntityUid uid)
    {

    }
}
