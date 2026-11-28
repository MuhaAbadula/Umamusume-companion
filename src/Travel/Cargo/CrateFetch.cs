using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class CompanionDriver
    {
        const float FetchRadius = 15f * Souvenirs.UPerM;
        GameObject fetchBox;
        internal bool Fetching => fetching;
        bool fetching, carrying;
        Vector3 lastFetchPosition, parkedSeat;
        float stalled, checkSpaceAt;
        InventoryLogicC fetchBoot;
        Collider[] carryColliders;
        bool[] colliderStates;
        bool wasKinematic;
        Transform crateGroundParent;
        bool crateWasTracked;
        int bootLeg, detours;
        float bootSide;
        Vector3 bypassPoint;
        bool walkingBypass;
        int bypassAttempts;

        // Walk out past the open door, along the body, then across behind the bumper.
        Vector3 BootWaypoint(UncleLogicC u, Vector3 actor, out bool atBoot)
        {
            var car = u.carLogic.GetComponent<CarLogicC>();
            Vector3 passenger = car.rightDoor != null ? car.rightDoor.transform.position : u.uncleCarTransform.position;
            Vector3 driver = car.leftDoor != null ? car.leftDoor.transform.position : car.playerSeat.transform.position;
            Vector3 middle = (driver + passenger) * 0.5f;
            Vector3 rear = Flat(fetchBoot.transform.position - middle).normalized;
            Vector3 bumper = fetchBoot.transform.position + rear * (2f * walkLegLen);
            if (car.bootStandingLoc != null)
            {
                bumper = car.bootStandingLoc.position;
                rear = Flat(bumper - middle).normalized;
            }
            Vector3 across = Vector3.Cross(Vector3.up, rear);
            float longitudinal = Vector3.Dot(actor - bumper, rear);
            float lateral = Vector3.Dot(actor - middle, across);
            var bounds = GetComponent<CharacterController>().bounds;
            float radius = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float width = Mathf.Abs(Vector3.Dot(passenger - driver, across)) * 0.5f + radius + (0.75f + detours) * walkLegLen;
            Vector3 toBoot = Flat(bumper - actor);
            atBoot = toBoot.magnitude <= 1.4f * walkLegLen && Mathf.Abs(actor.y - bumper.y) <= bounds.size.y;
            if (atBoot || ClearCrateWalk(actor, bumper, radius)) return bumper;
            if (bootSide == 0f)
            {
                bootSide = lateral >= 0f ? 1f : -1f;
                if (longitudinal >= 0f) bootLeg = 2;
            }
            if (bootLeg == 0 && lateral * bootSide >= width - 0.35f * walkLegLen) bootLeg++;
            if (bootLeg == 1 && longitudinal >= 0.65f * walkLegLen) bootLeg++;
            if (bootLeg == 2) return bumper;
            Vector3 outside = actor + across * (width * bootSide - lateral);
            Vector3 corner = outside + rear * (walkLegLen - longitudinal);
            if (bootLeg == 0 && ClearCrateWalk(actor, corner, radius)) bootLeg = 1;
            return bootLeg == 0 ? outside : corner;
        }

        bool ClearCrateWalk(Vector3 from, Vector3 to, float radius)
        {
            Vector3 path = Flat(to - from);
            if (path.sqrMagnitude < 0.01f) return true;
            float half = Mathf.Max(0f, GetComponent<CharacterController>().bounds.extents.y - radius - 0.35f * walkLegLen);
            foreach (var hit in UnityEngine.Physics.CapsuleCastAll(from - Vector3.up * half, from + Vector3.up * half,
                radius, path.normalized, path.magnitude,
                ~0, QueryTriggerInteraction.Ignore))
                if (hit.normal.y < 0.7f && !hit.transform.IsChildOf(transform) &&
                    !hit.collider.transform.IsChildOf(fetchBox.transform)) return false;
            return true;
        }

        // Outside the road's path grid the controller walks straight. If it actually
        // stalls, pick a reachable turn around the obstruction before resuming the job.
        bool FindCrateBypass(Bounds actor, Vector3 goal)
        {
            float radius = Mathf.Max(actor.extents.x, actor.extents.z);
            Vector3 forward = Flat(goal - actor.center).normalized;
            float best = float.PositiveInfinity;
            for (int i = 1; i <= 6; i++)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 dir = Quaternion.AngleAxis(side * i * 25f, Vector3.up) * forward;
                    Vector3 point = actor.center + dir * (3f + Mathf.Min(3, bypassAttempts)) * walkLegLen;
                    RaycastHit ground;
                    if (!UnityEngine.Physics.Raycast(point + Vector3.up * walkLegLen, Vector3.down, out ground,
                        actor.size.y + 2f * walkLegLen, ~0, QueryTriggerInteraction.Ignore) ||
                        ground.normal.y < 0.7f || ground.collider.attachedRigidbody != null ||
                        Mathf.Abs(ground.point.y - actor.min.y) > walkLegLen ||
                        !ClearCrateWalk(actor.center, point, radius)) continue;
                    point.y = ground.point.y + actor.extents.y;
                    float score = Flat(goal - point).magnitude;
                    if (!ClearCrateWalk(point, goal, radius)) score += 8f * walkLegLen;
                    if (score >= best) continue;
                    best = score;
                    bypassPoint = point;
                }
            }
            return best < float.PositiveInfinity;
        }

        public string FetchKey()
        {
            if (fetching) return "Already fetching a crate.";
            var u = OutU();
            if (Time.timeScale == 0 || u == null || u.carLogic == null ||
                CarSpeed() > 1f || (outState == Out.InCar ? ExitBlocked(u) != null || !DoorClear(u) :
                (outState != Out.Follow && outState != Out.Wait) || MornHolding))
                return "Stop with the passenger door clear first.";
            var car = u.carLogic.GetComponent<CarLogicC>();
            fetchBoot = car.bootInventory.GetComponent<InventoryLogicC>();
            GameObject nearest = null;
            float best = float.PositiveInfinity;
            Vector3 seat = car.playerSeat.transform.position;
            foreach (var contents in FindObjectsOfType<BoxContentsC>())
            {
                var box = contents.gameObject;
                if (!IsLooseCrate(box)) continue;
                float distance = (box.transform.position - seat).sqrMagnitude;
                if (distance > best) continue;
                nearest = box;
                best = distance;
            }
            if (nearest == null) return "No roadside crate nearby.";
            if (best > FetchRadius * FetchRadius) return "Roadside crate out of range.";
            fetchLid = car.bootLid != null ? car.bootLid.GetComponent<DoorLogicC>() : null;
            if (fetchLid == null) return "Boot lid unavailable.";
            fetchBox = nearest;
            parkedSeat = u.uncleCarTransform.position;
            fetching = true;
            stalled = 0f;
            checkSpaceAt = 0f;
            lastFetchPosition = transform.position;
            if (outState == Out.InCar) StartCoroutine(ExitCar(u));
            else
            {
                outState = Out.Follow;
                var ai = GetComponent<Pathfinding.AIPath>();
                if (ai != null) ai.target = outT;
            }
            return "Fetching the nearest roadside crate.";
        }

        static bool IsLooseCrate(GameObject box)
        {
            var body = box.GetComponent<Rigidbody>();
            var pickup = box.GetComponent<ObjectPickupC>();
            var collider = box.GetComponent<BoxCollider>();
            if (!box.activeInHierarchy || body == null || collider == null || !collider.enabled ||
                pickup == null || !pickup.isPurchased || pickup.isInShoppingKart || pickup.isTweenTransition ||
                pickup.inventoryPlacedAt != null) return false;
            var parent = box.transform.parent;
            return parent == null || (!body.isKinematic && parent.GetComponentInParent<HoldingLogicC>() == null &&
                                      parent.GetComponentInParent<InventoryLogicC>() == null &&
                                      parent.GetComponentInParent<ObjectPickupC>() == null);
        }

        DoorLogicC fetchLid;
        bool openedLid, reaching;
        float reachTime, liftTime, lidReadyAt, handleWeight, gripWidth;
        Vector3 crateBottomLocal;
        Vector3 liftStart;
        Transform placingSlot;
        float slideTime;
        Vector3 slideStart;
        Quaternion slideRotation;

        void PoseCrateHands()
        {
            if (life == null || fetchBox == null || (!carrying && !reaching)) return;
            Vector3 side = transform.right;
            Vector3 center = fetchBox.transform.TransformPoint(crateBottomLocal);
            Vector3 right = center + side * gripWidth;
            if (fetchLid != null && handleWeight > 0f)
            {
                Transform handle = fetchLid.handle != null ? fetchLid.handle.transform : fetchLid.transform;
                right = Vector3.Lerp(right, handle.position, handleWeight);
            }
            life.CrateHands(center - side * gripWidth, right, side, transform.forward, carrying ? 1f : Mathf.Clamp01(reachTime / 0.4f));
        }

        void FaceCrateTarget(Vector3 position, float dt)
        {
            Vector3 direction = Flat(position - transform.position);
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), dt * 240f);
        }

        bool OpenForLoading(float dt)
        {
            if (fetchLid == null) return false;
            if (!fetchLid.open)
            {
                if (fetchLid.isLocked || Time.time < lidReadyAt) return false;
                handleWeight = Mathf.MoveTowards(handleWeight, 1f, dt * 3f);
                if (handleWeight < 1f) return false;
                fetchLid.StartCoroutine(fetchLid.Trigger());
                openedLid = fetchLid.open;
                lidReadyAt = Time.time + 1.45f;
                return false;
            }
            handleWeight = Mathf.MoveTowards(handleWeight, 0f, dt * 2f);
            return Time.time >= lidReadyAt;
        }

        internal void EndFetch()
        {
            if (carrying && fetchBox != null)
            {
                fetchBox.transform.SetParent(crateGroundParent, true);
                fetchBox.GetComponent<Rigidbody>().isKinematic = wasKinematic;
                var route = RouteGeneratorC.Global;
                if (crateWasTracked && route != null && !route.cratesToBeCleanedUp.Contains(fetchBox))
                {
                    fetchBox.GetComponent<ObjectPickupC>().isInGarbageCollection = route.cratesToBeCleanedUp.Count;
                    route.cratesToBeCleanedUp.Add(fetchBox);
                }
                for (int i = 0; i < carryColliders.Length; i++)
                    if (carryColliders[i] != null) carryColliders[i].enabled = colliderStates[i];
            }
            if (openedLid && fetchLid != null && fetchLid.open) fetchLid.StartCoroutine(fetchLid.Trigger());
            openedLid = reaching = false;
            reachTime = handleWeight = lidReadyAt = 0f;
            placingSlot = null;
            fetchLid = null;
            fetching = carrying = false;
            fetchBox = null;
            crateGroundParent = null;
            crateWasTracked = false;
            walkingBypass = false;
            bypassAttempts = 0;
            carryColliders = null;
            colliderStates = null;
        }

        void CarryCrate()
        {
            var body = fetchBox.GetComponent<Rigidbody>();
            var route = RouteGeneratorC.Global;
            crateGroundParent = fetchBox.transform.parent;
            int index = route != null ? route.cratesToBeCleanedUp.IndexOf(fetchBox) : -1;
            crateWasTracked = index >= 0;
            if (crateWasTracked)
            {
                route.cratesToBeCleanedUp.RemoveAt(index);
                route.UpdateGarbageCollection(index);
            }
            fetchBox.GetComponent<ObjectPickupC>().isInGarbageCollection = -1;
            wasKinematic = body.isKinematic;
            body.isKinematic = true;
            carryColliders = fetchBox.GetComponentsInChildren<Collider>();
            colliderStates = new bool[carryColliders.Length];
            for (int i = 0; i < carryColliders.Length; i++)
            {
                colliderStates[i] = carryColliders[i].enabled;
                carryColliders[i].enabled = false;
            }
            fetchBox.transform.SetParent(transform, true);
            liftStart = fetchBox.transform.localPosition;
            liftTime = 0f;
            fetchBox.transform.rotation = transform.rotation;
            Debug.Log("[Umamusume companion] crate picked up: " + fetchBox.name);
            bootSide = 0f;
            bootLeg = detours = 0;
            walkingBypass = false;
            bypassAttempts = 0;
            carrying = true;
            stalled = 0f;
        }

        void UpdateFetch(UncleLogicC u, float dt)
        {
            if (fetchBox == null || fetchBoot == null || Flat(u.uncleCarTransform.position - parkedSeat).magnitude > 4f * walkLegLen ||
                (!carrying && !IsLooseCrate(fetchBox)))
            {
                EndFetch();
                StartBoarding(u, "crate request interrupted");
                return;
            }
            var controller = GetComponent<CharacterController>();
            Bounds actor = controller.bounds;
            Vector3 target;
            bool withinReach;
            if (carrying)
            {
                if (placingSlot == null)
                {
                    liftTime += dt;
                    Vector3 held = actor.center + transform.forward * (0.65f * walkLegLen);
                    fetchBox.transform.localPosition = Vector3.Lerp(liftStart, transform.InverseTransformPoint(held),
                        Mathf.SmoothStep(0f, 1f, liftTime / 0.55f));
                }
                target = BootWaypoint(u, actor.center, out withinReach);
            }
            else
            {
                var collider = fetchBox.GetComponent<Collider>();
                if (collider == null) { EndFetch(); StartBoarding(u, "crate removed"); return; }
                target = collider.ClosestPoint(actor.center);
                Vector3 nearest = actor.ClosestPoint(target);
                withinReach = Vector3.Distance(target, nearest) <= Mathf.Max(0.45f, 0.8f * walkLegLen);
            }
            if (walkingBypass && (withinReach || Flat(actor.center - bypassPoint).magnitude <= 0.65f * walkLegLen))
                walkingBypass = false;
            outT.position = walkingBypass ? bypassPoint : target;
            if (withinReach)
            {
                outT.position = transform.position;
                FaceCrateTarget(carrying ? fetchBoot.transform.position : fetchBox.transform.position, dt);
                if (!carrying)
                {
                    reaching = true;
                    reachTime += dt;
                    var bounds = fetchBox.GetComponent<Collider>().bounds;
                    crateBottomLocal = fetchBox.transform.InverseTransformPoint(bounds.center - Vector3.up * bounds.extents.y);
                    gripWidth = Mathf.Clamp(Mathf.Min(bounds.extents.x, bounds.extents.z), walkLegLen * 0.18f, walkLegLen * 0.55f);
                    if (reachTime >= 0.45f) CarryCrate();
                }
                else if (OpenForLoading(dt) && (placingSlot != null || Time.time >= checkSpaceAt))
                {
                    checkSpaceAt = Time.time + 0.6f;
                    if (StowCrate())
                    {
                        EndFetch();
                        StartBoarding(u, "crate stowed");
                        return;
                    }
                }
            }
            else if (Flat(transform.position - lastFetchPosition).sqrMagnitude > 0.04f)
            {
                lastFetchPosition = transform.position;
                stalled = 0f;
            }
            else if ((stalled += dt) > 1.75f + bypassAttempts && bypassAttempts < 4)
            {
                walkingBypass = FindCrateBypass(actor, target);
                bypassAttempts++;
                if (walkingBypass)
                {
                    stalled = 0f;
                    outT.position = bypassPoint;
                }
            }
            else if (carrying && stalled > 3f)
            {
                // Give an open door extra clearance if its tip still blocks this lane.
                detours = Mathf.Min(detours + 1, 3);
                walkingBypass = false;
                bootLeg = 0;
                stalled = 0f;
            }
            else if (!carrying && stalled > 10f)
            {
                EndFetch();
                StartBoarding(u, "crate unreachable");
                return;
            }
            Steer(u, dt);
        }
    }
}
