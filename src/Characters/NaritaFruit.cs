using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class Life
    {
        enum FruitPhase { None, Offer, Taken, Declined }
        const float ExtendTime = 0.8f, OfferTime = 12f, RetractTime = 0.65f;
        FruitPhase fruitPhase;
        GameObject apricot;
        float fruitSince, fruitEnd, fruitSize;
        Vector3 takeFrom;
        bool fruitAnswered;

        internal bool OfferingFruit { get { return fruitPhase != FruitPhase.None; } }

        void BeginFruit(float now)
        {
            if (OfferingFruit || armL == null || Camera.main == null) return;
            if (apricot == null)
            {
                var mesh = PropMesh.Get("dried_apricot");
                var skin = PropMesh.Material("dried_apricot.png");
                if (mesh == null || skin == null) return;
                apricot = new GameObject("Taishin dried apricot");
                apricot.transform.SetParent(armL.Upper, false);
                apricot.AddComponent<MeshFilter>().sharedMesh = mesh;
                apricot.AddComponent<MeshRenderer>().sharedMaterial = skin;
            }
            if (NPlaying && StopGesture != null) StopGesture();
            ConsoleShow(false);
            fruitSince = now;
            fruitPhase = FruitPhase.Offer;
            fruitAnswered = false;
            fruitSize = 0.09f * ArmLen(armL);
            PoseFruit(now);
            apricot.SetActive(true);
            Attend(Look.Player, 3f, 1f);
            Say("narita_fruit_1");
            CompanionMod.Current?.Notify(GameText.Choose("Click the dried apricot to take it", "Нажми на курагу, чтобы взять её"), 6f);
        }

        bool FruitAimed()
        {
            if (fruitPhase != FruitPhase.Offer || apricot == null || !apricot.activeSelf ||
                Time.timeScale <= 0f || Time.time < fruitSince + ExtendTime || Time.time >= fruitSince + OfferTime) return false;
            var camera = Camera.main;
            if (camera == null) return false;
            var drag = camera.GetComponent<DragRigidbodyC>();
            if (drag != null && (drag.isHolding1 != null || (drag.inputRestrict && !drag.inputRestrictDropOnly))) return false;
            Vector3 delta = apricot.transform.position - camera.transform.position;
            float along = Vector3.Dot(delta, camera.transform.forward);
            return along > 0f && along < (drag != null ? drag.maxRayDistance : 4.25f) &&
                (delta - camera.transform.forward * along).sqrMagnitude <= fruitSize * fruitSize;
        }

        void TakeFruit(float now)
        {
            if (fruitPhase != FruitPhase.Offer) return;
            takeFrom = apricot.transform.position;
            fruitPhase = FruitPhase.Taken;
            fruitEnd = now;
        }

        void TickFruit(float now)
        {
            if (fruitPhase == FruitPhase.Offer && now - fruitSince >= OfferTime)
            {
                fruitPhase = FruitPhase.Declined;
                fruitEnd = now;
            }
            if (fruitPhase == FruitPhase.Offer) return;
            if (!fruitAnswered && (CanSay == null || CanSay()))
            {
                Say(fruitPhase == FruitPhase.Taken ? "narita_fruit_2" : "narita_fruit_3");
                fruitAnswered = true;
            }
            if (now - fruitEnd >= RetractTime)
            {
                apricot.SetActive(false);
                if (fruitAnswered) CancelFruit();
            }
        }

        void CancelFruit()
        {
            if (!OfferingFruit) return;
            if (apricot != null) apricot.SetActive(false);
            fruitPhase = FruitPhase.None;
            nPlayOkAt = Time.time + 1f;
        }

        internal void PoseFruit(float now)
        {
            var cam = Camera.main;
            if (cam == null || armL == null || apricot == null) return;
            Vector3 forward, up;
            Facing(out forward, out up);
            Vector3 left = Vector3.Cross(forward, up).normalized;
            float length = ArmLen(armL);
            float weight = fruitPhase == FruitPhase.Offer ? Mathf.SmoothStep(0f, 1f, (now - fruitSince) / ExtendTime)
                : 1f - Mathf.SmoothStep(0f, 1f, (now - fruitEnd) / RetractTime);
            Vector3 reach = (left * 0.8f + forward * 0.5f - up * 0.18f).normalized;
            HandToL(ShoulderL + reach * (length * 0.82f), -up + left * 0.2f, reach, weight);
            if (!apricot.activeSelf && fruitPhase != FruitPhase.Offer) return;
            // Rest the fruit across the base of the fingers, just above her open hand.
            Vector3 palm = Vector3.Lerp(PalmL, TipL, 0.35f) + up * (fruitSize * 0.22f);
            apricot.transform.position = palm;
            apricot.transform.rotation = Quaternion.LookRotation(forward, up);
            Transform parent = apricot.transform.parent;
            float scale = Mathf.Max(0.001f, Mathf.Abs(parent.lossyScale.x));
            apricot.transform.localScale = Vector3.one * (fruitSize / scale);
            if (fruitPhase == FruitPhase.Taken)
            {
                // The driver draws the piece out of her palm and down below their view.
                float t = Mathf.SmoothStep(0f, 1f, (now - fruitEnd) / RetractTime);
                Vector3 destination = cam.transform.position + cam.transform.forward * 0.25f - cam.transform.up * 0.35f;
                apricot.transform.position = Vector3.Lerp(takeFrom, destination, t);
            }
        }
    }
}
