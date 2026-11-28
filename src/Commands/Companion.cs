using UnityEngine;

namespace UmamusumeCompanion
{
    internal enum Command { Exit, Follow, Stop, Board, Fetch }

    internal sealed class Companion
    {
        readonly CompanionDriver driver;
        readonly UncleLogicC actor;

        public Companion(CompanionDriver driver)
        {
            this.driver = driver;
            actor = driver.GetComponent<UncleLogicC>();
        }

        public bool Present => driver != null && driver.isActiveAndEnabled && actor != null &&
                               !actor.uncleGoneForever && !actor.uncleAtHome;

        internal static string Absent => GameText.Choose("Your companion isn't nearby", "Умы сейчас нет рядом");
        static string Busy => GameText.Choose("She's busy right now", "Ума сейчас занята");
        static string Exiting => GameText.Choose("She's getting out of the car", "Ума выходит из машины");
        static string Following => GameText.Choose("She's following you", "Ума идёт за тобой");

        public string Unavailable(Command cmd)
        {
            if (!Present) return Absent;
            if (CompanionDriver.MorningWalk.IsWalking(driver) && !actor.readyToBoard)
                return GameText.Choose("She's walking to the car", "Ума идёт к машине");
            var state = driver.OutingState;
            if (state == CompanionDriver.Out.Exiting) return Exiting;
            if (state == CompanionDriver.Out.Boarding || actor.readyToBoard)
                return GameText.Choose("She's getting into the car", "Ума садится в машину");
            if (driver.MornHolding) return GameText.Choose("She's waiting in the motel room", "Ума ждёт в номере");
            bool seated = state == CompanionDriver.Out.InCar && actor.isSat;
            bool outside = state == CompanionDriver.Out.Follow || state == CompanionDriver.Out.Wait;
            bool fetching = driver.Fetching;
            switch (cmd)
            {
                case Command.Exit: return seated ? null : GameText.Choose("She's already outside", "Ума уже снаружи");
                case Command.Follow:
                    if (state == CompanionDriver.Out.Follow && !fetching)
                        return GameText.Choose("She's already following you", "Ума уже идёт за тобой");
                    break;
                case Command.Stop:
                    if (state == CompanionDriver.Out.Wait) return GameText.Choose("She's already waiting", "Ума уже ждёт");
                    break;
                case Command.Board:
                    if (seated) return GameText.Choose("She's already in the car", "Ума уже в машине");
                    if (actor.uncleCarTransform == null || actor.uncleDoorTrigger == null ||
                        actor.carNavBlocker == null || actor.relayNodeTransform == null)
                        return GameText.Choose("She can't get to the car", "Машина недоступна");
                    break;
                case Command.Fetch:
                    if (fetching) return GameText.Choose("She's already fetching a crate", "Ума уже забирает ящик");
                    if (seated || outside) return null;
                    break;
            }
            return outside ? null : seated ? GameText.Choose("Ask her to get out first", "Сначала попроси Уму выйти") : Busy;
        }

        public string Run(Command cmd)
        {
            string reason = Unavailable(cmd);
            if (reason != null) return reason;
            if (cmd == Command.Fetch) return Feedback(driver.FetchKey());
            if (driver.Fetching)
            {
                driver.EndFetch();
                // A fetch temporarily uses the follow state with the crate as target.
                // Once cancelled, CompanionDriver refreshes the player target on its next update.
                if (cmd == Command.Follow) return Following;
            }
            if (cmd == Command.Board)
            {
                driver.StartBoarding(actor, "command wheel");
                return GameText.Choose("She's heading back to the car", "Ума возвращается в машину");
            }
            if (cmd == Command.Exit) Doze.OnG();
            return Feedback(driver.OutingKey());
        }

        static string Feedback(string result)
        {
            switch (result)
            {
                case "Outing: she gets out.": return Exiting;
                case "Outing: she waits here.": return GameText.Choose("She's waiting here", "Ума ждёт здесь");
                case "Outing: she comes along.": return Following;
                case "Outing: the car is moving.": return GameText.Choose("Stop the car first", "Сначала останови машину");
                case "Outing: her door is blocked.": return GameText.Choose("Clear the passenger door", "Освободи место у пассажирской двери");
                case "Outing: not now, talking.": return GameText.Choose("Wait for her to finish speaking", "Подожди, пока Ума договорит");
                case "Outing: not now, at the border.": return GameText.Choose("Wait until you've cleared the border", "Подожди до выезда с границы");
                case "Outing: not now, at the motel (she gets out there anyway).":
                    return GameText.Choose("She'll get out on her own at the motel", "У мотеля Ума выходит сама");
                case "Outing: not now, repair tutorial.": return GameText.Choose("Finish the repairs first", "Сначала закончи ремонт");
                case "Outing: not now, asleep.": return GameText.Choose("She's asleep", "Ума спит");
                case "Fetching the nearest roadside crate.": return GameText.Choose("She's heading for the crate", "Ума идёт за ящиком");
                case "No roadside crate nearby.": return GameText.Choose("No crate within 15 m of the car", "В радиусе 15 м от машины нет ящика");
                case "Roadside crate out of range.": return GameText.Choose("Drive within 15 m of the crate", "Подъедь к ящику на 15 м или ближе");
                case "Boot lid unavailable.": return GameText.Choose("The boot is unavailable", "Багажник недоступен");
                case "Stop with the passenger door clear first.":
                    return GameText.Choose("Stop the car with the passenger door clear", "Останови машину и освободи пассажирскую дверь");
                default: return Busy;
            }
        }
    }
}
