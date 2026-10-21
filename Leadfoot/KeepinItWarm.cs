using System;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using FiveFR._API.Attributes;
using FiveFR._API.Classes;
using FiveFR._API.Entities;
using FiveFR._API.Extensions;
using FiveFR._API.Radio;
using FiveFR._API.Services;

namespace FiveFR.Storylines.Leadfoot
{
    /// <summary>Episode 2 — the inverse of Episode 1: no traffic stop. Lit up, Mabel treats the officer as an escort and drives home.</summary>
    [AddonProperties("Leadfoot Keeping It Warm", "DevKilo", "1.0.0")]
    public class KeepinItWarm : Callout
    {
        private const string Ep1Key = "Leadfoot.Ep1";
        private const string FlagKey = "Leadfoot.Ep2";
        private const string DingedKey = FlagKey + ".CarDinged";
        private const string Tag = "[LeadfootWarm]";

        private const float CruiseSpeed = 22f;          // m/s, a "fast cruise"
        private const int TickMs = 500;
        private const float WarnRange = 3.5f;
        private const float EngageRange = 80f;          // officer must be this close for lights to count as an escort
        private const int StalledMs = 3000;             // stationary this long, away from home = stopped early
        private const float HomeRadius = 25f;
        private const int EscortGraceMs = 45000;        // at home, officer trailed her all the way and never engaged her
        private const string HandbagClipset = "move_f@handbag";

        // TODO: verify against the map. A wide loop, roughly 1-2.5 km out from the Randal house, so the
        // chase has room to happen before she turns for home.
        private static readonly Vector3[] Loop =
        {
            new Vector3(215.0f, -810.0f, 30.7f),        // Pillbox Hill
            new Vector3(-540.0f, -214.0f, 37.6f),       // Rockford Plaza
            new Vector3(-1038.0f, -1180.0f, 4.5f),      // Vespucci / Del Perro
            new Vector3(-360.0f, -1730.0f, 20.5f),      // Rancho, west side
            new Vector3(380.0f, -1480.0f, 29.3f)        // Davis, east side
        };

        private static readonly string[] RouteLines =
        {
            "Tyler says warming it up means gentle. Tyler also pays the insurance, so.",
            "Keep up, dear — I can't be late home. James has been alone for almost an hour, and that is how incidents happen."
        };

        private enum Phase { Loop, Escort, Home, Stopped, Done }

        private Suspect Mabel;
        private CalloutVehicle Vehicle;
        private Phase _phase = Phase.Loop;
        private Ep1Outcome _ep1;
        private bool _resolved;
        private bool _engaged;
        private bool _stoppedEarly;
        private bool _touched;                  // officer has actually dealt with her on foot
        private bool _dialogueDone;
        private CitationFiledHandler _onCitation;
        private const DrivingStyle Style = DrivingStyle.SwerveAroundCars | DrivingStyle.IgnoreLights;

        public KeepinItWarm()
        {
            ShortName = "Leadfoot Keeping It Warm";
            CalloutDescription =
                "Callers report an orange sports car being driven very fast by a very old lady. She has waved at several of them.";
            ResponseCode = 2;
            StartDistance = 150f;
            InitInfo(Loop[0]);
        }

        private bool Interactive => _phase == Phase.Home || _phase == Phase.Stopped;

        // Ended stays false after FinishCallout, so IsCleanedUp covers both ways out.
        private bool Live => !Ended && !IsCleanedUp && !_resolved && Mabel != null && Mabel.Status.IsAlive;

        private bool VehicleLost => Vehicle?.Vehicle != null && (!Vehicle.Vehicle.Exists() || Vehicle.Vehicle.IsDead);

        /// <summary>Unlocks once Episode 1 has been completed with any outcome.</summary>
        public override Task<bool> CheckRequirements() => Task.FromResult(
            StorylineFlags.Get(Ep1Key, Ep1Outcome.None) != Ep1Outcome.None &&
            WorldZone.GetZoneAtPosition(Game.PlayerPed.Position).County.Equals(nameof(WorldZone.EWorldZoneCounty.LosSantos)));

        public override async Task OnAccept()
        {
            _ep1 = StorylineFlags.Get(Ep1Key, Ep1Outcome.None);

            var mabel = new Profiles.MabelRandal();
            var jester = new Profiles.TylerJester();

            Mabel = await SpawnSuspect(mabel.Hash, Loop[0].Around(5f), 0f);
            Vehicle = await SpawnCalloutVehicle(jester.VehicleHash, new Vector4(Loop[0].X, Loop[0].Y, Loop[0].Z, 0f));
            Vehicle.WhenHere(v =>
            {
                v.Mods.LicensePlate = jester.VehiclePlate;
                v.Mods.PrimaryColor = jester.VehicleColor;
                v.Mods.SecondaryColor = jester.VehicleColor;
            });

            // Identity is set before anything can read it.
            var names = mabel.Name.Split(' ');
            Mabel.Identity.Set(names[0], names[1], mabel.DOB);

            Mabel.Inventory.Add("Butterscotch candies");
            Mabel.Inventory.Add("Bingo dauber");
            Mabel.Inventory.Add("Reading glasses");
            Mabel.Inventory.Add("Tyler's registration (plastic sleeve, sticky note)");
            Mabel.Demeanor = Demeanor.Cooperative;

            foreach (var recordWarrant in Mabel.Record.Warrants.ToList())
            {
                if (Enum.TryParse<Offense>(recordWarrant, out var @case))
                    Mabel.Record.Drop(@case);
            }

            InitBlip(80f, BlipColor.Red);
            _ = RadioIntro();
        }

        public override async void OnStart(Ped player)
        {
            try
            {
                HookEvents();
                NotificationService.InfoNotify("Find the orange Jester. She thinks you're her escort.", "Objective");

                var car = await Vehicle.Arrived();
                await QueueService.Predicate(() => !Ended && !IsCleanedUp && Mabel.Ped == null, 100, 15000);
                if (Ended || IsCleanedUp || Mabel.Ped == null || car == null) return;

                // Spawned already rolling: seat her and go before anyone is close enough to notice.
                Mabel.Ped.SetIntoVehicle(car, VehicleSeat.Driver);
                Mabel.Blip.Show(BlipColor.Red, null, "Speeding Vehicle");
                SetDriving();

                _ = Guard(nameof(Monitor), Monitor);
                _ = Guard(nameof(Cruise), Cruise);
            }
            catch (Exception e)
            {
                DiagnosticLog.WriteLine($"{Tag} OnStart failed: {e}", isError: true);
                Unhook();
                EndCallout();
            }
        }

        public override void OnCancelAfter() => Unhook();

        /// <summary>Runs a fire-and-forget beat so a failure is logged instead of lost.</summary>
        private static async Task Guard(string name, Func<Task> body)
        {
            try
            {
                await body();
            }
            catch (Exception e)
            {
                DiagnosticLog.WriteLine($"{Tag} {name} failed: {e}", isError: true);
            }
        }

        // ---- dialogue ------------------------------------------------------------------

        private Task Say(string line) => Mabel.Speak(line);

        /// <summary>Speaks a line, then pauses. False when the moment has passed (resolved, left, or ended).</summary>
        private async Task<bool> Beat(string line, int pauseAfterMs = 0)
        {
            if (!Live || !Interactive) return false;
            await Say(line);
            if (pauseAfterMs > 0) await BaseScript.Delay(pauseAfterMs);
            return Live && Interactive;
        }

        /// <summary>One follow-up transmission, selected by how Episode 1 ended. Fled wins: it is the most distinctive memory.</summary>
        private string Ep1Callback()
        {
            if ((_ep1 & Ep1Outcome.Fled) != 0)
                return "Last time, she left mid-stop and went to bingo. Maybe don't spend forty minutes in your cruiser on this one.";

            switch (_ep1 & ~Ep1Outcome.Fled)
            {
                case Ep1Outcome.Warned:
                    return "Last unit gave her a warning. She's added it to the collection, if you're wondering.";
                case Ep1Outcome.Cited:
                    return "She framed that ticket, by the way. Hallway. Next to the wedding photo.";
                case Ep1Outcome.Arrested:
                    return "Since the arrest she has told the entire bingo hall there is no bingo in jail. It comes up a lot.";
                default:
                    return null;
            }
        }

        private async Task RadioIntro()
        {
            await BaseScript.Delay(20000);
            if (Ended || IsCleanedUp) return;

            RadioApi.Transmit(new RadioTransmission()
            {
                Text = "Be advised... it's Mabel again. Different car.",
                Replies =
                [
                    new RadioReply("What car?",
                            "A Jester. It belongs to her grandson Tyler. She says she's keeping it warm for him. It has been warm for forty minutes.")
                        { Say = "Dispatch, what do you mean, different car?" },
                    new RadioReply("Is she fleeing?",
                            "Negative. She waved at the reporting unit. I believe she thinks you're her escort.")
                        { Say = "Dispatch, is she running from me?" },
                    new RadioReply("10-4.",
                            "Copy. Try to keep up. ...I can't believe I just said that about Mabel Randal.")
                        { Say = "10-4, I'm on her." }
                ],
                Priority = RadioPriority.Priority
            });

            var callback = Ep1Callback();
            if (callback == null) return;

            await BaseScript.Delay(8000);
            if (Ended || IsCleanedUp) return;
            RadioApi.Transmit(new RadioTransmission() { Text = callback, Priority = RadioPriority.Routine });
        }

        // ---- the drive -------------------------------------------------------------------

        private void SetDriving()
        {
            // OR the flags, never add them: the presets overlap bits and "+" corrupts them.
            Mabel.Ped.MaxDrivingSpeed = CruiseSpeed + 6f;
            Mabel.Ped.DrivingSpeed = CruiseSpeed;
            Mabel.Ped.DrivingStyle = Style;
        }

        private void DriveTo(Vector3 target)
        {
            SetDriving();
            Mabel.Ped.Task.DriveTo(Vehicle.Vehicle, target, 8f, CruiseSpeed, (int)Style);
        }

        private bool OfficerLitUpNearby()
        {
            var player = Game.PlayerPed;
            var car = player.CurrentVehicle;
            return car != null && car.IsSirenActive &&
                   player.Position.DistanceToSquared(Vehicle.Position) <= EngageRange * EngageRange;
        }

        /// <summary>Loops the neighbourhood until the officer lights her up, then escorts herself home.</summary>
        private async Task Cruise()
        {
            var next = 0;
            DriveTo(Loop[next]);

            while (Live && !_engaged)
            {
                await BaseScript.Delay(TickMs);
                if (!Live) return;

                if (OfficerLitUpNearby())
                {
                    _engaged = true;
                    break;
                }

                if (Vehicle.Position.DistanceToSquared(Loop[next]) < 15f * 15f)
                {
                    next = (next + 1) % Loop.Length;
                    DriveTo(Loop[next]);
                }
            }
            if (!Live) return;

            // Lights on: she does not flee and does not stop. She waves, and takes her escort home.
            _phase = Phase.Escort;
            await Say("Oh lovely, an escort! Leadfoot Randal travels in style today.");
            if (!Live) return;
            DriveTo(Profiles.RandalHouse.Driveway);

            var lineIndex = 0;
            var drivingMs = 0;
            while (Live && _phase == Phase.Escort &&
                   Vehicle.Position.DistanceToSquared(Profiles.RandalHouse.Driveway) > HomeRadius * HomeRadius)
            {
                await BaseScript.Delay(1000);
                if (!Live || _phase != Phase.Escort) return;
                if (Vehicle.Vehicle.Speed < 1f) continue;       // only talks while actually driving

                drivingMs += 1000;
                if (drivingMs >= 12000 && lineIndex < RouteLines.Length)
                {
                    drivingMs = 0;
                    await Say(RouteLines[lineIndex++]);
                }
            }
            if (!Live || _phase != Phase.Escort) return;

            await ArriveHome();
        }

        /// <summary>Stationary away from home while an officer is on her = the car was stopped by force (block / PIT / spikes).</summary>
        private async Task Monitor()
        {
            var stalledMs = 0;
            while (!Ended && !IsCleanedUp && !_resolved)
            {
                await BaseScript.Delay(TickMs);

                if (Mabel == null || !Mabel.Status.IsAlive || VehicleLost)
                {
                    OnKilled();
                    return;
                }

                if (_phase != Phase.Escort) { stalledMs = 0; continue; }

                var awayFromHome = Vehicle.Position.DistanceToSquared(Profiles.RandalHouse.Driveway) > HomeRadius * HomeRadius;
                var officerNear = Game.PlayerPed.Position.DistanceToSquared(Vehicle.Position) <= EngageRange * EngageRange;
                stalledMs = awayFromHome && officerNear && Vehicle.Vehicle.Speed < 1f ? stalledMs + TickMs : 0;
                if (stalledMs >= StalledMs)
                {
                    stalledMs = 0;
                    await StoppedEarly();
                }
            }
        }

        private async Task ArriveHome()
        {
            // Parks perfectly, then leaves the car like she's been doing it all her life.
            Mabel.Ped.Task.ParkVehicle(Vehicle.Vehicle, Profiles.RandalHouse.Driveway, Profiles.RandalHouse.DrivewayHeading, 3f, false);
            await QueueService.Predicate(() => Live && _phase == Phase.Escort && Vehicle.Vehicle.Speed > 0.5f, TickMs, 20000);
            if (!Live || _phase != Phase.Escort) return;

            Mabel.Ped.Task.LeaveVehicle(Vehicle.Vehicle, LeaveVehicleFlags.None);
            await QueueService.Predicate(() => Live && Mabel.Ped.IsInVehicle(), TickMs, 10000);
            if (!Live || _phase != Phase.Escort) return;

            Mabel.Blip.Hide();
            _phase = Phase.Home;
            await WalkToDoor();
            if (!Live) return;

            _ = Guard(nameof(Watch), Watch);
        }

        private async Task WalkToDoor()
        {
            API.RequestAnimSet(HandbagClipset);
            await QueueService.Predicate(() => !API.HasAnimSetLoaded(HandbagClipset), 100, 3000);
            if (!Live) return;
            API.SetPedMovementClipset(Mabel.Ped.Handle, HandbagClipset, 1f);

            Mabel.Move.To(Profiles.RandalHouse.FrontDoor, false);
            await QueueService.Predicate(() => Live && Mabel.Position.DistanceToSquared(Profiles.RandalHouse.FrontDoor) > 2f * 2f, TickMs, 30000);
            if (Live) Mabel.Move.Hold();
        }

        /// <summary>Block / PIT / spikes: she gets out, furious about the car and not the stop.</summary>
        private async Task StoppedEarly()
        {
            if (!Live || _stoppedEarly || _phase != Phase.Escort) return;
            _stoppedEarly = true;
            _phase = Phase.Stopped;
            StorylineFlags.Set(DingedKey, Ep2Outcome.CarDinged);

            Mabel.Blip.Hide();
            Mabel.Ped.Task.ClearAll();
            Mabel.Ped.Task.LeaveVehicle(Vehicle.Vehicle, LeaveVehicleFlags.None);
            await QueueService.Predicate(() => Live && Mabel.Ped.IsInVehicle(), TickMs, 10000);
            if (!Live) return;

            await Say("That is Tyler's car. Tyler is the only person in this family allowed to dent it, and believe me, he's tried.");
            if (!Live) return;

            _ = Guard(nameof(Watch), Watch);
        }

        // ---- the resolution on foot --------------------------------------------------------

        private void HookEvents()
        {
            Mabel.On.Identified.Then(args => _ = OnIdentified());
            Mabel.On.Arrested.Then(args => _ = OnArrested());
            Mabel.On.Booked.Then(args => _ = OnBooked());
            Mabel.On.Killed.Then(args => OnKilled());

            _onCitation = e =>
            {
                var subject = e.Subject;
                var isMabel = e.SubjectNetId == Mabel.Ped.NetworkId ||
                              (subject != null && subject.FirstName == Mabel.Identity.FirstName && subject.LastName == Mabel.Identity.LastName);
                if (isMabel && Live) _ = OnCited();
            };
            FilingEvents.CitationFiled += _onCitation;
        }

        private void Unhook()
        {
            if (_onCitation == null) return;
            FilingEvents.CitationFiled -= _onCitation;
            _onCitation = null;
        }

        /// <summary>Warn input, and the soft "escorted" close if the officer just follows her home.</summary>
        private async Task Watch()
        {
            var graceMs = 0;
            while (Live)
            {
                await BaseScript.Delay(TickMs);
                if (!Live || !Interactive) continue;

                var near = Game.PlayerPed.Position.DistanceToSquared(Mabel.Position) <= WarnRange * WarnRange;
                if (_dialogueDone && !Game.PlayerPed.IsInVehicle() && near &&
                    ControlService.IsControlJustPressed(0, Control.Context))
                {
                    await Warn();
                    return;
                }

                // Followed her all the way home, never tried a stop, never dealt with her: dispatch closes it.
                if (_phase == Phase.Home && _engaged && !_stoppedEarly && !_touched &&
                    Game.PlayerPed.Position.DistanceToSquared(Mabel.Position) <= EngageRange * EngageRange)
                {
                    graceMs += TickMs;
                    if (graceMs >= EscortGraceMs)
                    {
                        await Escorted();
                        return;
                    }
                }
            }
        }

        private Task OnIdentified() => Guard(nameof(OnIdentified), async () =>
        {
            _touched = true;
            NotificationService.ShowNotification("~f~Mabel~s~ hands over everything, in order: license, insurance, library card, and Tyler's registration in a plastic sleeve.");

            // Faster than last time: she has streamlined her system since Episode 1.
            if (!await Beat("Leadfoot Randal. License, insurance, bingo card, library card, and that's Tommy, he's eight. All in order of likelihood.", 3000)) return;
            if (!await Beat("The registration is Tyler's, it's in the sleeve. He left a note on it. Very neat handwriting, for a boy.", 3500)) return;
            if (!await Beat("'DO NOT DRIVE FAST — T.' Yes, I read it. It says don't drive fast. It doesn't say anything about warm.", 4000)) return;
            if (!await Beat("A butterscotch, dear? Let's call this a warning between friends. James would be so proud.")) return;

            // Dispatch waits until she has finished talking.
            _ = DispatchReply("Mabel Randal, seventy-nine. License valid, no warrants, no priors. Dispatch notes she has, quote, streamlined her system.");

            _dialogueDone = true;
            NotificationService.InfoNotify("Press ~INPUT_CONTEXT~ near Mabel to let her go with a warning, or cite / arrest her as usual.", "Resolution");
        });

        private Task DispatchReply(string text) => Guard(nameof(DispatchReply), async () =>
        {
            await BaseScript.Delay(1500);
            if (Ended || IsCleanedUp || !Mabel.Status.IsAlive) return;
            RadioApi.Transmit(new RadioTransmission() { Text = text, Priority = RadioPriority.Routine });
        });

        // ---- resolutions -----------------------------------------------------------------

        private void Resolve(Ep2Outcome outcome)
        {
            _resolved = true;
            _touched = true;
            StorylineFlags.Set(FlagKey, outcome);
        }

        private Task Warn() => Guard(nameof(Warn), async () =>
        {
            Resolve(Ep2Outcome.Warned);
            await Say("Another one for the fridge. Leadfoot strikes again.");
            Mabel.Move.To(Profiles.RandalHouse.FrontDoor, false);
            await BaseScript.Delay(4000);
            Finish();
        });

        private Task OnCited() => Guard(nameof(OnCited), async () =>
        {
            Resolve(Ep2Outcome.Cited);
            await Say("Framed by Friday. Tyler can hang it in his hallway.");
            await BaseScript.Delay(2000);
            Finish();
        });

        private Task OnArrested() => Guard(nameof(OnArrested), async () =>
        {
            Resolve(Ep2Outcome.Arrested);
            Mabel.Surrender();
            await Say("Well. At least this time I already know about the bingo.");
        });

        private Task OnBooked() => Guard(nameof(OnBooked), async () =>
        {
            var repeatCustomer = (_ep1 & Ep1Outcome.Arrested) != 0;
            RadioApi.Transmit(new RadioTransmission()
            {
                Text = repeatCustomer
                    ? "Tell them Leadfoot is a repeat customer. They'll know about the bingo."
                    : "Advise transport — subject is asking whether repeat guests get a discount.",
                Priority = RadioPriority.Routine
            });
            await BaseScript.Delay(4000);
            Finish();
        });

        /// <summary>Soft completion: the officer followed her all the way home without ever attempting a stop.</summary>
        private Task Escorted() => Guard(nameof(Escorted), async () =>
        {
            Resolve(Ep2Outcome.Escorted);
            RadioApi.Transmit(new RadioTransmission()
            {
                Text = "You have successfully escorted Mabel Randal home. That is going in the report exactly like that.",
                Priority = RadioPriority.Priority
            });
            await BaseScript.Delay(4000);
            Finish();
        });

        /// <summary>She or the Jester is gone. No XP, no radio banter, no punchline.</summary>
        private void OnKilled()
        {
            if (_phase == Phase.Done) return;
            _resolved = true;
            _phase = Phase.Done;
            Unhook();
            EndCallout();
        }

        private void Finish()
        {
            _phase = Phase.Done;
            Unhook();
            FinishCallout();
        }
    }
}
