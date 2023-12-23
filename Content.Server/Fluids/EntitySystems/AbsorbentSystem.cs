using Content.Server.Popups;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Interaction;
using Content.Shared.Timing;
using Content.Shared.Weapons.Melee;
using Robust.Server.Audio;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Fluids.EntitySystems;

/// <inheritdoc/>
public sealed class AbsorbentSystem : SharedAbsorbentSystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly PopupSystem _popups = default!;
    [Dependency] private readonly PuddleSystem _puddleSystem = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SolutionContainerSystem _solutionSystem = default!;
    [Dependency] private readonly UseDelaySystem _useDelay = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AbsorbentComponent, ComponentInit>(OnAbsorbentInit);
        SubscribeLocalEvent<AbsorbentComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<AbsorbentComponent, InteractNoHandEvent>(OnInteractNoHand);
        SubscribeLocalEvent<AbsorbentComponent, SolutionChangedEvent>(OnAbsorbentSolutionChange);
    }

    private void OnAbsorbentInit(EntityUid uid, AbsorbentComponent component, ComponentInit args)
    {
        // TODO: I know dirty on init but no prediction moment.
        UpdateAbsorbent(uid, component);
    }

    private void OnAbsorbentSolutionChange(EntityUid uid, AbsorbentComponent component, SolutionChangedEvent args)
    {
        UpdateAbsorbent(uid, component);
    }

    private void UpdateAbsorbent(EntityUid uid, AbsorbentComponent component)
    {
        if (!_solutionSystem.TryGetSolution(uid, AbsorbentComponent.SolutionName, out var solution))
            return;

        var oldProgress = component.Progress.ShallowClone();
        component.Progress.Clear();

        var water = solution.GetTotalPrototypeQuantity(PuddleSystem.EvaporationReagents);
        if (water > FixedPoint2.Zero)
        {
            component.Progress[solution.GetColorWithOnly(_prototype, PuddleSystem.EvaporationReagents)] = water.Float();
        }

        var otherColor = solution.GetColorWithout(_prototype, PuddleSystem.EvaporationReagents);
        var other = (solution.Volume - water).Float();

        if (other > 0f)
        {
            component.Progress[otherColor] = other;
        }

        var remainder = solution.AvailableVolume;

        if (remainder > FixedPoint2.Zero)
        {
            component.Progress[Color.DarkGray] = remainder.Float();
        }

        if (component.Progress.Equals(oldProgress))
            return;

        Dirty(uid, component);
    }

    private void OnInteractNoHand(EntityUid uid, AbsorbentComponent component, InteractNoHandEvent args)
    {
        if (args.Handled || args.Target == null)
            return;

        args.Handled = Mop(uid, args.Target.Value, uid, component);
    }

    private void OnAfterInteract(EntityUid uid, AbsorbentComponent component, AfterInteractEvent args)
    {
        if (!args.CanReach || args.Handled || args.Target == null)
            return;

        args.Handled = Mop(args.User, args.Target.Value, args.Used, component);
    }

    private bool Mop(EntityUid user, EntityUid target, EntityUid used, AbsorbentComponent component)
    {
        if (!_solutionSystem.TryGetSolution(used, AbsorbentComponent.SolutionName, out var absorbentSolution))
            return false;

        if (_useDelay.ActiveDelay(used))
            return false;

        // If it's a puddle try to grab from
        if (!TryPuddleInteract(user, used, target, component, absorbentSolution))
        {
            // If it's refillable try to transfer
            if (!TryRefillableInteract(user, used, target, component, absorbentSolution))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Logic for an absorbing entity interacting with a refillable.
    /// </summary>
    private bool TryRefillableInteract(EntityUid user, EntityUid used, EntityUid target, AbsorbentComponent component, Solution absorbentSolution)
    {
        if (!TryComp(target, out RefillableSolutionComponent? refillable))
            return false;

        if (!_solutionSystem.TryGetRefillableSolution(target, out var refillableSolution, refillable: refillable))
            return false;

        if (refillableSolution.Volume <= 0)
        {
            // Target empty - only transfer absorbent contents into refillable
            if (!TryTransferFromAbsorbentToRefillable(user, used, target, component, absorbentSolution, refillableSolution))
                return false;
        }
        else
        {
            // Target non-empty - do a two-way transfer
            if (!TryTwoWayAbsorbentRefillableTransfer(user, used, target, component, absorbentSolution, refillableSolution))
                return false;
        }

        _audio.PlayPvs(component.TransferSound, target);
        _useDelay.BeginDelay(used);
        return true;
    }

    /// <summary>
    ///     Logic for an transferring solution from absorber to an empty refillable.
    /// </summary>
    private bool TryTransferFromAbsorbentToRefillable(
        EntityUid user,
        EntityUid used,
        EntityUid target,
        AbsorbentComponent component,
        Solution absorbentSolution,
        Solution refillableSolution)
    {
        if (absorbentSolution.Volume <= 0)
        {
            _popups.PopupEntity(Loc.GetString("mopping-system-target-container-empty", ("target", target)), user, user);
            return false;
        }

        var transferAmount = component.PickupAmount < refillableSolution.AvailableVolume ?
            component.PickupAmount :
            refillableSolution.AvailableVolume;

        if (transferAmount <= 0)
        {
            _popups.PopupEntity(Loc.GetString("mopping-system-full", ("used", used)), used, user);
            return false;
        }

        // Prioritize transferring non-evaporatives if absorbent has any
        var contaminants = absorbentSolution.SplitSolutionWithout(transferAmount, PuddleSystem.EvaporationReagents);
        if (contaminants.Volume > 0)
        {
            _solutionSystem.UpdateChemicals(used, absorbentSolution, true);
            _solutionSystem.TryAddSolution(target, refillableSolution, contaminants);
        }
        else
        {
            var evaporatives = absorbentSolution.SplitSolution(transferAmount);
            _solutionSystem.UpdateChemicals(used, absorbentSolution, true);
            _solutionSystem.TryAddSolution(target, refillableSolution, evaporatives);
        }

        return true;
    }

    /// <summary>
    ///     Logic for an transferring contaminants to a non-empty refillable & reabsorbing water if any available.
    /// </summary>
    private bool TryTwoWayAbsorbentRefillableTransfer(
        EntityUid user,
        EntityUid used,
        EntityUid target,
        AbsorbentComponent component,
        Solution absorbentSolution,
        Solution refillableSolution)
    {
        var contaminantsFromAbsorbent = absorbentSolution.SplitSolutionWithout(component.PickupAmount, PuddleSystem.EvaporationReagents);
        _solutionSystem.UpdateChemicals(used, absorbentSolution, true);

        if (contaminantsFromAbsorbent.Volume == FixedPoint2.Zero && absorbentSolution.AvailableVolume == FixedPoint2.Zero)
        {
            // Nothing to transfer to refillable and no room to absorb anything extra
            _popups.PopupEntity(Loc.GetString("mopping-system-puddle-space", ("used", used)), user, user);

            // We can return cleanly because nothing was split from absorbent solution
            return false;
        }

        var waterPulled = component.PickupAmount < absorbentSolution.AvailableVolume ?
            component.PickupAmount :
            absorbentSolution.AvailableVolume;

        var waterFromRefillable = refillableSolution.SplitSolutionWithOnly(waterPulled, PuddleSystem.EvaporationReagents);
        _solutionSystem.UpdateChemicals(target, refillableSolution);

        if (waterFromRefillable.Volume == FixedPoint2.Zero && contaminantsFromAbsorbent.Volume == FixedPoint2.Zero)
        {
            // Nothing to transfer in either direction
            _popups.PopupEntity(Loc.GetString("mopping-system-target-container-empty-water", ("target", target)), user, user);

            // We can return cleanly because nothing was split from refillable solution
            return false;
        }

        var anyTransferOccurred = false;

        if (waterFromRefillable.Volume > FixedPoint2.Zero)
        {
            // transfer water to absorbent
            _solutionSystem.TryAddSolution(used, absorbentSolution, waterFromRefillable);
            anyTransferOccurred = true;
        }

        if (contaminantsFromAbsorbent.Volume > 0)
        {
            if (refillableSolution.AvailableVolume <= 0)
            {
                _popups.PopupEntity(Loc.GetString("mopping-system-full", ("used", target)), user, user);
            }
            else
            {
                // transfer as much contaminants to refillable as will fit
                var contaminantsForRefillable = contaminantsFromAbsorbent.SplitSolution(refillableSolution.AvailableVolume);
                _solutionSystem.TryAddSolution(target, refillableSolution, contaminantsForRefillable);
                anyTransferOccurred = true;
            }

            // absorb everything that did not fit in the refillable back by the absorbent
            _solutionSystem.TryAddSolution(used, absorbentSolution, contaminantsFromAbsorbent);
        }

        return anyTransferOccurred;
    }

    /// <summary>
    ///     Logic for an absorbing entity interacting with a puddle.
    /// </summary>
    private bool TryPuddleInteract(EntityUid user, EntityUid used, EntityUid target, AbsorbentComponent absorber, Solution absorberSoln)
    {
        if (!TryComp(target, out PuddleComponent? puddle))
            return false;

        if (!_solutionSystem.TryGetSolution(target, puddle.SolutionName, out var puddleSoln) || puddleSoln.Volume <= 0)
            return false;

        // Check if the puddle has any non-evaporative reagents
        if (_puddleSystem.CanFullyEvaporate(puddleSoln))
        {
            _popups.PopupEntity(Loc.GetString("mopping-system-puddle-evaporate", ("target", target)), user, user);
            return true;
        }

        // Check if we have any evaporative reagents on our absorber to transfer
        var available = absorberSoln.GetTotalPrototypeQuantity(PuddleSystem.EvaporationReagents);

        // No material
        if (available == FixedPoint2.Zero)
        {
            _popups.PopupEntity(Loc.GetString("mopping-system-no-water", ("used", used)), user, user);
            return true;
        }

        var transferMax = absorber.PickupAmount;
        var transferAmount = available > transferMax ? transferMax : available;

        var puddleSplit = puddleSoln.SplitSolutionWithout(transferAmount, PuddleSystem.EvaporationReagents);
        var absorberSplit = absorberSoln.SplitSolutionWithOnly(puddleSplit.Volume, PuddleSystem.EvaporationReagents);

        // Do tile reactions first
        var coordinates = Transform(target).Coordinates;
        if (_mapManager.TryGetGrid(coordinates.GetGridUid(EntityManager), out var mapGrid))
        {
            _puddleSystem.DoTileReactions(mapGrid.GetTileRef(coordinates), absorberSplit);
        }

        puddleSoln.AddSolution(absorberSplit, _prototype);
        absorberSoln.AddSolution(puddleSplit, _prototype);

        _solutionSystem.UpdateChemicals(used, absorberSoln);
        _solutionSystem.UpdateChemicals(target, puddleSoln);
        _audio.PlayPvs(absorber.PickupSound, target);
        _useDelay.BeginDelay(used);

        var userXform = Transform(user);
        var targetPos = _transform.GetWorldPosition(target);
        var localPos = _transform.GetInvWorldMatrix(userXform).Transform(targetPos);
        localPos = userXform.LocalRotation.RotateVec(localPos);

        _melee.DoLunge(user, used, Angle.Zero, localPos, null, false);

        return true;
    }
}
