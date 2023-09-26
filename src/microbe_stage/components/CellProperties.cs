﻿namespace Components
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DefaultEcs;
    using DefaultEcs.Command;
    using Godot;
    using Newtonsoft.Json;

    /// <summary>
    ///   Base properties of a microbe (separate from the species info as early multicellular species object couldn't
    ///   work there)
    /// </summary>
    public struct CellProperties
    {
        /// <summary>
        ///   Base colour of the cell. This is used when initializing organelles as it would otherwise be difficult to
        ///   to obtain the colour
        /// </summary>
        public Color Colour;

        public float UnadjustedRadius;

        // public float MassFromOrganelles

        public MembraneType MembraneType;
        public float MembraneRigidity;

        /// <summary>
        ///   The membrane created for this cell. This is here so that some other systems apart from the visuals system
        ///   can have access to the membrane data.
        /// </summary>
        [JsonIgnore]
        public Membrane? CreatedMembrane;

        public bool IsBacteria;

        /// <summary>
        ///   Set to false when shape needs to be recreated
        /// </summary>
        [JsonIgnore]
        public bool ShapeCreated;

        public CellProperties(ICellProperties initialProperties)
        {
            Colour = initialProperties.Colour;
            MembraneType = initialProperties.MembraneType;
            MembraneRigidity = initialProperties.MembraneRigidity;
            CreatedMembrane = null;
            IsBacteria = initialProperties.IsBacteria;

            // These are initialized later
            UnadjustedRadius = 0;

            // TODO: do we need to copy some more properties?

            ShapeCreated = false;
        }

        public float Radius => IsBacteria ? UnadjustedRadius * 0.5f : UnadjustedRadius;
    }

    public static class CellPropertiesHelpers
    {
        /// <summary>
        ///   The default visual position if the organelle is on the microbe's center
        ///   TODO: this should be made organelle type specific, chemoreceptors and pilus should point backward (in Godot
        ///   coordinates to point forwards by default, and flagella should keep this current default value).
        ///   Actually latest issue describing how this could be solved is:
        ///   https://github.com/Revolutionary-Games/Thrive/issues/3620 and
        ///   https://github.com/Revolutionary-Games/Thrive/issues/3109
        /// </summary>
        public static readonly Vector3 DefaultVisualPos = Vector3.Forward;

        public delegate void ModifyDividedCellCallback(ref EntityRecord entity);

        /// <summary>
        ///   Checks this cell and also the entire colony if something can enter engulf mode in it
        /// </summary>
        public static bool CanEngulfInColony(this ref CellProperties cellProperties, in Entity entity)
        {
            if (entity.Has<MicrobeColony>())
            {
                ref var colony = ref entity.Get<MicrobeColony>();

                colony.CanEngulf();
            }

            return cellProperties.MembraneType.CanEngulf;
        }

        /// <summary>
        ///   Checks can a cell engulf a target entity. This is the preferred way to check instead of directly using
        ///   just the <see cref="Engulfer"/> check as this also validates the cell has the right properties to be able
        ///   to engulf.
        /// </summary>
        public static EngulfCheckResult CanEngulfObject(this ref CellProperties cellProperties,
            ref SpeciesMember cellSpecies, ref Engulfer engulfer, in Entity target)
        {
            // Membranes with Cell Wall cannot engulf
            if (!cellProperties.MembraneType.CanEngulf)
                return EngulfCheckResult.NotInEngulfMode;

            return engulfer.CanEngulfObject(cellSpecies.ID, in target);
        }

        /// <summary>
        ///   Checks that membrane is created and ready. Various cell operations require the membrane to be ready
        ///   before they can be used.
        /// </summary>
        public static bool IsMembraneReady(this ref CellProperties cellProperties)
        {
            if (cellProperties.CreatedMembrane == null)
                return false;

            return !cellProperties.CreatedMembrane.Dirty;
        }

        /// <summary>
        ///   Throws some compound out of this Microbe, up to maxAmount
        /// </summary>
        /// <param name="cellProperties">Cell to use to calculate where to eject the compounds</param>
        /// <param name="cellPosition">
        ///   The position the cell is currently at to use it as a base for the emission location calculation
        /// </param>
        /// <param name="compounds">The cell's current compounds to eject from</param>
        /// <param name="compoundCloudSystem">Cloud system to emit to</param>
        /// <param name="compound">The compound type to eject</param>
        /// <param name="maxAmount">The maximum amount to eject</param>
        /// <param name="direction">The direction in which to eject relative to the microbe</param>
        /// <param name="displacement">How far away from the microbe to eject</param>
        /// <returns>The amount of emitted compound, can be less than the <see cref="maxAmount"/></returns>
        public static float EjectCompound(this ref CellProperties cellProperties, ref WorldPosition cellPosition,
            CompoundBag compounds, CompoundCloudSystem compoundCloudSystem, Compound compound, float maxAmount,
            Vector3 direction, float displacement = 0)
        {
            float amount = compounds.TakeCompound(compound, maxAmount);

            cellProperties.SpawnEjectedCompound(ref cellPosition, compoundCloudSystem, compound, amount, direction,
                displacement);
            return amount;
        }

        /// <summary>
        ///   Triggers reproduction on this cell (even if not ready)
        /// </summary>
        /// <remarks>
        ///   <para>
        ///     Now with multicellular colonies are also allowed to divide so there's no longer a check against that
        ///   </para>
        /// </remarks>
        public static void Divide(this ref CellProperties cellProperties, ref OrganelleContainer organelles,
            in Entity entity, Species species, IWorldSimulation worldSimulation, ISpawnSystem spawnerToRegisterWith,
            ModifyDividedCellCallback? customizeCallback)
        {
            if (organelles.Organelles == null)
                throw new InvalidOperationException("Organelles not initialized");

            if (entity.Has<MicrobeColonyMember>())
                throw new ArgumentException("Cell that is a colony member (non-leader) can't divide");

            ref var position = ref entity.Get<WorldPosition>();

            var currentPosition = position.Position;

            // Find the direction to the right from where the cell is facing
            var direction = position.Rotation.Xform(Vector3.Right);

            // Start calculating separation distance
            // TODO: fix for multihex organelles
            var organellePositions = organelles.Organelles.Select(o => Hex.AxialToCartesian(o.Position)).ToList();

            // TODO: switch this to using membrane radius as that'll hopefully fix the last few divide bugs
            float distanceRight =
                MathUtils.GetMaximumDistanceInDirection(Vector3.Right, Vector3.Zero, organellePositions);
            float distanceLeft =
                MathUtils.GetMaximumDistanceInDirection(Vector3.Left, Vector3.Zero, organellePositions);

            if (entity.Has<MicrobeColony>())
            {
                // Bigger separation for cell colonies

                throw new NotImplementedException();

                // TODO: there is still a problem with colonies being able to spawn inside each other

                // var colonyMembers = Colony.ColonyMembers.Select(c => c.GlobalTransform.origin);
                //
                // distanceRight += MathUtils.GetMaximumDistanceInDirection(direction, currentPosition, colonyMembers);
            }

            float width = distanceLeft + distanceRight + Constants.DIVIDE_EXTRA_DAUGHTER_OFFSET;

            if (cellProperties.IsBacteria)
                width *= 0.5f;

            Dictionary<Compound, float> reproductionCompounds;

            // This method only supports microbe and early multicellular species
            if (species is MicrobeSpecies microbeSpecies)
            {
                reproductionCompounds = microbeSpecies.CalculateTotalComposition();
            }
            else
            {
                reproductionCompounds = ((EarlyMulticellularSpecies)species).Cells[0].CalculateTotalComposition();
            }

            var spawnPosition = currentPosition + direction * width;

            // Create the one daughter cell.
            var (recorder, weight) = SpawnHelpers.SpawnMicrobeWithoutFinalizing(worldSimulation, species, spawnPosition,
                true, null, out var copyEntity);

            // Since the daughter spawns right next to the cell, it should face the same way to avoid colliding
            // This probably wastes a bit of memory but should be fine to overwrite the WorldPosition component like
            // this
            copyEntity.Set(new WorldPosition(spawnPosition, position.Rotation));

            // TODO: should this also set an initial look direction that is the same?

            // Make it despawn like normal
            spawnerToRegisterWith.NotifyExternalEntitySpawned(copyEntity,
                Constants.MICROBE_SPAWN_RADIUS * Constants.MICROBE_SPAWN_RADIUS, weight);

            // Remove the compounds from the created cell
            var originalCompounds = entity.Get<CompoundStorage>().Compounds;

            // Copying the capacity should be fine like this as the original cell should be reset to the normal
            // capacity already so
            var copyEntityCompounds = new CompoundBag(originalCompounds.Capacity);

            var keys = new List<Compound>(originalCompounds.Compounds.Keys);

            bool isPlayerMicrobe = entity.Has<PlayerMarker>();

            // Split the compounds between the two cells.
            foreach (var compound in keys)
            {
                var amount = originalCompounds.GetCompoundAmount(compound);

                if (amount <= 0)
                    continue;

                // If the compound is for reproduction we give player and NPC microbes different amounts.
                if (reproductionCompounds.TryGetValue(compound, out float divideAmount))
                {
                    // The amount taken away from the parent cell depends on if it is a player or NPC. Player
                    // cells always have 50% of the compounds they divided with taken away.
                    float amountToTake = amount * 0.5f;

                    if (!isPlayerMicrobe)
                    {
                        // NPC parent cells have at least 50% taken away, or more if it would leave them
                        // with more than 90% of the compound it would take to immediately divide again.
                        amountToTake = Math.Max(amountToTake, amount - (divideAmount * 0.9f));
                    }

                    originalCompounds.TakeCompound(compound, amountToTake);

                    // Since the child cell is always an NPC they are given either 50% of the compound from the
                    // parent, or 90% of the amount required to immediately divide again, whichever is smaller.
                    float amountToGive = Math.Min(amount * 0.5f, divideAmount * 0.9f);
                    var addedCompound = copyEntityCompounds.AddCompound(compound, amountToGive);

                    if (addedCompound < amountToGive)
                    {
                        // TODO: handle the excess compound that didn't fit in the other cell
                    }
                }
                else
                {
                    // Non-reproductive compounds just always get split evenly to both cells.
                    originalCompounds.TakeCompound(compound, amount * 0.5f);

                    var amountAdded = copyEntityCompounds.AddCompound(compound, amount * 0.5f);

                    if (amountAdded < amount)
                    {
                        // TODO: handle the excess compound that didn't fit in the other cell
                    }
                }
            }

            copyEntity.Set(new CompoundStorage
            {
                Compounds = copyEntityCompounds,
            });

            SpawnHelpers.FinalizeEntitySpawn(recorder, worldSimulation);

            if (entity.Has<SoundEffectPlayer>())
            {
                // Play the split sound
                ref var soundEffectPlayer = ref entity.Get<SoundEffectPlayer>();
                soundEffectPlayer.PlaySoundEffect("res://assets/sounds/soundeffects/reproduction.ogg");
            }
        }

        /// <summary>
        ///   Ejects compounds from the microbes behind position (taking direction into account), into the environment
        ///   (but doesn't remove it from the microbe storage, see <see cref="EjectCompound"/>)
        /// </summary>
        /// <remarks>
        ///   <para>
        ///     Note that the compounds ejected are created in this world and not taken from the microbe.
        ///     This is purely for adding the compound to the cloud system at the right position. Other methods need
        ///     to be used to remove the ejected compounds from this microbe.
        ///   </para>
        /// </remarks>
        /// <returns>
        ///   True on success, false if the membrane was not ready yet (this error should be checked for by the caller
        ///   before even calling this method)
        /// </returns>
        public static bool SpawnEjectedCompound(this ref CellProperties cellProperties, ref WorldPosition cellPosition,
            CompoundCloudSystem compoundCloudSystem, Compound compound, float amount, Vector3 direction,
            float displacement = 0)
        {
            var amountToEject = amount * Constants.MICROBE_VENT_COMPOUND_MULTIPLIER;

            if (amountToEject <= MathUtils.EPSILON)
                return true;

            if (!cellProperties.IsMembraneReady())
            {
                GD.PrintErr($"{nameof(SpawnEjectedCompound)} called before membrane is ready, ignoring eject");
                return false;
            }

            compoundCloudSystem.AddCloud(compound, amountToEject,
                cellProperties.CalculateNearbyWorldPosition(ref cellPosition, direction, displacement));

            return true;
        }

        /// <summary>
        ///   Calculates a world position for emitting compounds. Requires membrane to be valid already.
        /// </summary>
        public static Vector3 CalculateNearbyWorldPosition(this ref CellProperties cellProperties,
            ref WorldPosition cellPosition, Vector3 direction, float displacement = 0)
        {
            if (cellProperties.CreatedMembrane == null)
                throw new InvalidOperationException("Membrane not ready yet");

            // OLD CODE kept here in case we want a more accurate membrane position, also this code
            // produces an incorrect world position which needs fixing if this were to be used
            /*
            // The back of the microbe
            var exit = Hex.AxialToCartesian(new Hex(0, 1));
            var membraneCoords = Membrane.GetVectorTowardsNearestPointOfMembrane(exit.x, exit.z);

            // Get the distance to eject the compounds
            var ejectionDistance = Membrane.EncompassingCircleRadius;

            // The membrane radius doesn't take being bacteria into account
            if (CellTypeProperties.IsBacteria)
                ejectionDistance *= 0.5f;

            float angle = 180;

            // Find the direction the microbe is facing
            var yAxis = Transform.basis.y;
            var microbeAngle = Mathf.Atan2(yAxis.x, yAxis.y);
            if (microbeAngle < 0)
            {
                microbeAngle += 2 * Mathf.Pi;
            }

            microbeAngle = microbeAngle * 180 / Mathf.Pi;

            // Take the microbe angle into account so we get world relative degrees
            var finalAngle = (angle + microbeAngle) % 360;

            var s = Mathf.Sin(finalAngle / 180 * Mathf.Pi);
            var c = Mathf.Cos(finalAngle / 180 * Mathf.Pi);

            var ejectionDirection = new Vector3(-membraneCoords.x * c + membraneCoords.z * s, 0,
                membraneCoords.x * s + membraneCoords.z * c);

            return Translation + (ejectionDirection * ejectionDistance);
            */

            // Unlike the commented block of code above, this uses cheap membrane radius to calculate
            // distance for cheaper computations
            var distance = cellProperties.CreatedMembrane.EncompassingCircleRadius;

            // The membrane radius doesn't take being bacteria into account
            if (cellProperties.IsBacteria)
                distance *= 0.5f;

            distance += displacement;

            var ejectionDirection = cellPosition.Rotation.Xform(direction);

            var result = cellPosition.Position + (ejectionDirection * distance);

            return result;
        }

        public static Vector3 CalculateExternalOrganellePosition(this ref CellProperties cellProperties,
            Hex hexPosition, int orientation, out Quat rotation)
        {
            // TODO: https://github.com/Revolutionary-Games/Thrive/issues/3109
            _ = orientation;

            var membrane = cellProperties.CreatedMembrane;
            if (membrane == null)
            {
                throw new InvalidOperationException(
                    "Membrane is missing for cell properties, can't get external position");
            }

            var organellePos = Hex.AxialToCartesian(hexPosition);

            Vector3 middle = Hex.AxialToCartesian(new Hex(0, 0));
            var relativeOrganellePosition = middle - organellePos;

            if (relativeOrganellePosition == Vector3.Zero)
                relativeOrganellePosition = DefaultVisualPos;

            Vector3 exit = middle - relativeOrganellePosition;
            var membraneCoords = membrane.GetVectorTowardsNearestPointOfMembrane(exit.x,
                exit.z);

            var calculatedNewAngle = GetExternalOrganelleAngle(relativeOrganellePosition);

            rotation = MathUtils.CreateRotationForExternal(calculatedNewAngle);

            return membraneCoords;
        }

        public static void ApplyMembraneWigglyness(this ref CellProperties cellProperties, Membrane targetMembrane)
        {
            targetMembrane.WigglyNess = cellProperties.MembraneType.BaseWigglyness -
                (cellProperties.MembraneRigidity /
                    cellProperties.MembraneType.BaseWigglyness) * 0.2f;

            targetMembrane.MovementWigglyNess = cellProperties.MembraneType.MovementWigglyness -
                (cellProperties.MembraneRigidity /
                    cellProperties.MembraneType.BaseWigglyness) * 0.2f;
        }

        /// <summary>
        ///   Gets the angle of rotation of an externally placed organelle
        /// </summary>
        /// <param name="delta">The difference between the cell middle and the external organelle position</param>
        private static float GetExternalOrganelleAngle(Vector3 delta)
        {
            float angle = Mathf.Atan2(-delta.z, delta.x);
            if (angle < 0)
            {
                angle += 2 * Mathf.Pi;
            }

            angle = (angle * 180 / Mathf.Pi - 90) % 360;
            return angle;
        }
    }
}
