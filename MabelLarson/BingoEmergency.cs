using System;
using System.Collections.Generic;
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

namespace FiveFR.Storylines
{
    [AddonProperties("Leadfoot Bingo", "DevKilo", "1.0.0")]
    public class BingoEmergency : Callout
    {
        private const string FlagKey = "Leadfoot.Ep1";
        private const string PlaysKey = FlagKey + ".Plays";
        private const string Tag = "[LeadfootBingo]";

        private const float RushedSpeed = 24f;      // m/s, "well over the limit"
        private const float SpeedLimit = 13.4f;     // m/s, roughly 30 mph
        private const int TickMs = 500;
        private const int IdlePressureMs = 20000;   // between pressure lines
        private const int AbandonMs = 30000;        // in the cruiser mid-stop before she leaves
        private const float WarnRange = 3.5f;

        private static readonly string[] PressureLines =
        {
            "I can feel my lucky numbers expiring.",
            "Dear. Dear. The balls wait for no one.",
            "Doris is warming my seat as we speak. I can feel it from here.",
            "Leadfoot Randal has never once been late to a full house, dear. Never once.",
            "If I'm not home soon my James will start looking for me. In the car.",
            "They'll be on the second card by now. The second card!",
            "I'm not saying I'll cry. I'm saying I have a very full handbag of tissues."
        };

        private const int RideLineGapMs = 14000;    // of actual driving between back-seat lines

        private static readonly string[] RideLines =
        {
            "You're going quite slowly, dear. Leadfoot Randal could have you there in half the time.",
            "Is that the radio? Lovely. Is it the bingo caller? It isn't. Pity.",
            "Doris will have my seat by now. I hope you're happy. I'm sure you're very nice.",
            "If you see a cream Emperor on the way, that's mine. Somebody should be watching it. Somebody is, aren't they?",
            "I have butterscotches in my handbag, if you could just reach back — oh. Cuffs. Never mind.",
            "My James will be wondering where I've got to. He'll have the car out. Oh dear. He mustn't have the car out.",
            "Is it much further? I'd hate to miss the second card, even from in here."
        };

        private static readonly string[] FriskLines =
        {
            "Oh! Well. I don't usually let anyone do this before the second date.",
            "Again? Leadfoot Randal has nothing to hide but a handbag full of tissues.",
            "You're very thorough. James is thorough. He once searched the whole house for his keys. They were in the car. They're always in the car.",
            "If you find my lucky numbers, dear, I'd like those back.",
            "I'm beginning to think you just like holding my handbag."
        };

        private int _frisks;
        private bool _friskTalking;

        private Vector4 _Location =
            new Vector4(-960.67614746094f, -1194.3326416016f, 4.2044520378113f, 35.721240997314f);

        // TODO: verify both against the map before shipping.
        private Vector3 _HallLot = new Vector3(-1127.4f, -1456.9f, 4.9f);
        private const float HallLotHeading = 125f;
        private Vector3 _HallDoor = new Vector3(-1118.6f, -1450.2f, 5.1f);

        private enum Phase { Chase, Stopped, Bolting, Hall, Done }

        private Suspect Mabel;
        private CalloutVehicle Vehicle;
        private Phase _phase = Phase.Chase;
        private bool _fled;
        private bool _resolved;
        private bool _dialogueDone;
        private int _idleMs;
        private int _awayMs;
        private int _pressureIndex;
        private CitationFiledHandler _onCitation;
        private VehicleIdentityAssignedHandler _onPlate;
        private bool _plateRun;
        private bool _booked;
        private int _plays;                 // earlier encounters carried through to a resolution
        private Ep1Outcome _previous;       // how the last one ended (None on a first meeting)

        public BingoEmergency()
        {
            ShortName = "Leadfoot Bingo";
            CalloutDescription =
                "Reports of a sedan running reds and doing well over the limit near the community hall. Caller says — and I quote — 'it's that old lady again.'";
            ResponseCode = 2;
            StartDistance = 150f;
            InitInfo(_Location);
        }

        private bool Repeat => _plays > 0;
        private Ep1Outcome PreviousResolution => _previous & ~Ep1Outcome.Fled;
        private bool PreviouslyFled => (_previous & Ep1Outcome.Fled) != 0;

        private bool Interactive => _phase == Phase.Stopped || _phase == Phase.Hall;

        // Ended stays false after FinishCallout, so IsCleanedUp covers both ways out.
        private bool Live => !Ended && !IsCleanedUp && !_resolved && Mabel != null && Mabel.Status.IsAlive;

        public override Task<bool> CheckRequirements() => Task.FromResult(WorldZone.GetZoneAtPosition(Game.PlayerPed.Position).County.Equals(nameof(WorldZone.EWorldZoneCounty.LosSantos)));

        public override async Task OnAccept()
        {
            _plays = StorylineFlags.GetCount(PlaysKey);
            _previous = StorylineFlags.Get(FlagKey, Ep1Outcome.None);

            var profile = new Profiles.MabelRandal();
            Mabel = await SpawnSuspect(profile.Hash, Location.Around(5f), _Location.W);
            Vehicle = await SpawnCalloutVehicle(profile.VehicleHash, _Location);
            Vehicle.WhenHere(v =>
            {
                v.Mods.LicensePlate = profile.VehiclePlate;
                v.Mods.PrimaryColor = profile.VehicleColor;
                v.Mods.SecondaryColor = profile.VehicleColor;
            });

            // Completely clean: she is only a menace on the road.
            var names = profile.Name.Split(' ');
            Mabel.Identity.Set(names[0], names[1], profile.DOB);

            Mabel.Inventory.Add("Butterscotch candies");
            Mabel.Inventory.Add("Bingo dauber");
            Mabel.Inventory.Add("Reading glasses");
            Mabel.Demeanor = Demeanor.Cooperative;

            foreach (var recordWarrant in Mabel.Record.Warrants.ToList())
            {
                if (Enum.TryParse<Offense>(recordWarrant, out var @case))
                    Mabel.Record.Drop(@case);
            }

            // The car and the ped's body may not exist yet (SpawnCalloutVehicle returns a handle); seat her in OnStart.
            InitBlip(80f, BlipColor.Red);

            _ = RadioIntro();
        }

        public override async void OnStart(Ped player)
        {
            try
            {
                HookEvents();
                NotificationService.InfoNotify("Pull over Mabel.", "Objective");

                // The car and ped only exist once an officer is in range; wait for both before touching them.
                var car = await Vehicle.Arrived();
                await QueueService.Predicate(() => !Ended && !IsCleanedUp && Mabel.Ped == null, 100, 15000);
                if (Ended || IsCleanedUp || Mabel.Ped == null || car == null) return;

                // She only starts rolling once an officer is in range, so the chase can't leave before it begins.
                Mabel.Ped.SetIntoVehicle(car, VehicleSeat.Driver);
                Mabel.Blip.Show(BlipColor.Red, null, "Speeding Vehicle");
                DriveToHall();

                var approach = await Mabel.On.Approached(2f).Await();
                if (approach.CalloutEnded || !Live) return;

                // Predicate waits WHILE true: this holds until she has actually stopped.
                await QueueService.Predicate(() => Live && Vehicle.Vehicle.Speed >= 1f);
                if (!Live) return;

                // Parked up and cooperative; she only moves again if the player leaves her.
                Mabel.Ped.Task.ClearAll();
                Mabel.Blip.Hide();
                _phase = Phase.Stopped;
                HookPlateLookup();
                _ = Watch();

                await Say(ApproachLine());
            }
            catch (Exception e)
            {
                // Exceptions here end the callout silently in-game; leave a trace and end it properly.
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

        private async Task Say(string line)
        {
            _idleMs = 0;
            await Mabel.Speak(line);
            _idleMs = 0;
        }

        /// <summary>Speaks a line, then pauses. False when the moment has passed (resolved, left, or ended).</summary>
        private async Task<bool> Beat(string line, int pauseAfterMs = 0)
        {
            if (!Live || !Interactive) return false;
            await Say(line);
            if (pauseAfterMs > 0) await BaseScript.Delay(pauseAfterMs);
            return Live && Interactive;
        }

        /// <summary>Her first words at the window. Repeat callers get recognised, and reminded of how it went last time.</summary>
        private string ApproachLine()
        {
            const string Hurry = " I'm late, dear... Bingo starts in ten minutes and Doris will take my seat, she always takes my seat.";
            if (!Repeat)
                return "Oh, thank goodness, a police escort!" + Hurry;

            string opener;
            switch (PreviousResolution)
            {
                case Ep1Outcome.Warned:
                    opener = "Oh, it's you again, dear! I've still got your warning, it's on the fridge next to Tommy's drawings.";
                    break;
                case Ep1Outcome.Cited:
                    opener = "Oh, it's you again! Your ticket's framed in the hall, dear. James thinks it's a diploma.";
                    break;
                case Ep1Outcome.Arrested:
                    opener = "Oh. You. I've been told they do not have bingo in jail, and I'd like that on the record.";
                    break;
                default:
                    opener = "Oh, it's you again, dear!";
                    break;
            }
            return opener + Hurry + (PreviouslyFled ? " And I promise I'm staying put this time. Probably." : "");
        }

        /// <summary>Dispatch's answer to 'Mabel?', with a dry nod to the last encounter.</summary>
        private string WhoIsMabelReply()
        {
            const string Basics = "Mabel Randal, seventy-nine. Yes, that Randal — James's wife. Cream Emperor, plate M-A-B-E-L seventy-nine. ";
            const string Tail = "She's probably late again for bingo at the community hall, and she is not losing her seat to Doris. Again.";
            if (!Repeat) return Basics + Tail;

            string memory;
            switch (PreviousResolution)
            {
                case Ep1Outcome.Warned:
                    memory = "You let her off with a warning last time, and she has taken that as a lifestyle choice. ";
                    break;
                case Ep1Outcome.Cited:
                    memory = "You wrote her up last time. She framed it. ";
                    break;
                case Ep1Outcome.Arrested:
                    memory = "You booked her last time. The jail still gets asked about bingo. ";
                    break;
                default:
                    memory = "You know her. ";
                    break;
            }
            return "Mabel Randal, seventy-nine. You know the plate by now. " + memory +
                   (PreviouslyFled ? "She also left the stop last time, so keep her in sight. " : "") + Tail;
        }

        private Task OnIdentified() => Guard(nameof(OnIdentified), async () =>
        {
            _ = DispatchReply(Repeat
                ? "Mabel Randal, seventy-nine. License valid, no warrants. Dispatch notes she is already on file with this unit. Spotless record, apart from the file."
                : "Mabel Randal, seventy-nine. License valid, no warrants, no priors. Record is spotless apart from, quote, a certain enthusiasm for yellow lights.");
            NotificationService.ShowNotification("~f~Mabel~s~ hands over everything: library card, bingo membership, three grandchild photos.");

            if (!await Beat(Repeat
                    ? "Mabel Randal, dear. Leadfoot Randal, as you well know."
                    : "Mabel Randal, dear. Leadfoot Randal, if we're being formal.", 2500)) return;
            if (!await Beat("There's my license, registration, insurance, and that's Tommy, he's eight, he's very good at maths. Am I in much trouble? Be honest, I can tell.", 6000)) return;
            if (!await Beat("I have a system for the lights. If it's been yellow a while, that means it's committed. My James taught me.", 6000)) return;
            // The officer has read the licence; she corrects them.
            if (!await Beat("It's Leadfoot, dear. 'Mabel' is for the bingo hall and the DMV.", 5000)) return;
            if (!await Beat(Repeat
                    ? "Butterscotch? It's a fresh batch, dear. Take one and let's call this a warning between friends. Again. I still won't tell."
                    : "Take a candy and let's call this a warning between friends. I won't tell if you don't!")) return;

            _dialogueDone = true;
            NotificationService.InfoNotify("Press ~INPUT_CONTEXT~ near Mabel to let her go with a warning, or cite / arrest her as usual.", "Resolution");
        });

        private async Task RadioIntro()
        {
            await BaseScript.Delay(20000);
            if (Ended || IsCleanedUp) return;
            RadioApi.Transmit(new RadioTransmission()
            {
                Text = Repeat ? "Oh dear... Be advised... It's Mabel. Again." : "Oh dear... Be advised... It's Mabel.",
                Replies =
                [
                    new RadioReply("Mabel?",
                            WhoIsMabelReply())
                        { Say = "Dispatch, who's Mabel?" },
                    new RadioReply("The suspect?",
                            "Suspect identifies herself as 'Leadfoot.' This is self-reported. She's a grandmother with a lead foot, an otherwise clean record, and perfect parking. Once she's stopped she'll cooperate. Stopping her is the sport.")
                        { Say = "Dispatch... the suspect's name is Mabel?" },
                    new RadioReply("10-4.",
                            Repeat
                                ? "Copy. She will offer you a butterscotch. It is, I'm told, a fresh batch. What happens next is still between you and your conscience."
                                : "Copy. One warning, and it's the only one that matters: she will offer you a butterscotch. What happens next is between you and your conscience.")
                        { Say = "10-4, I'm en route." }
                ],
                Priority = RadioPriority.Priority
            });
        }

        // ---- the stop ------------------------------------------------------------------

        private void HookEvents()
        {
            Mabel.On.Identified.Then(args => _ = OnIdentified());
            Mabel.On.Arrested.Then(args => _ = OnArrested());
            Mabel.On.Booked.Then(args => _ = OnBooked());
            Mabel.On.Killed.Then(args => OnKilled());
            Mabel.On.Frisked.Then(args => _ = OnFrisked(args));

            _onCitation = e =>
            {
                var subject = e.Subject;
                var isMabel = e.SubjectNetId == Mabel.Ped.NetworkId ||
                              (subject != null && subject.FirstName == Mabel.Identity.FirstName && subject.LastName == Mabel.Identity.LastName);
                if (isMabel && Live) _ = OnCited();
            };
            FilingEvents.CitationFiled += _onCitation;
        }

        /// <summary>Hooked once she's stopped, so only a plate run during the stop gets the dispatch reply.</summary>
        private void HookPlateLookup()
        {
            _onPlate = e =>
            {
                if (_plateRun || !Live || e.VehicleNetId != Vehicle.NetId) return;
                _plateRun = true;
                _ = DispatchReply((Repeat ? "Plate MABEL79, again. " : "Plate MABEL79 ") + "returns to a cream Albany Emperor, registered to Mabel Randal. Registration current, insurance current, not stolen. Dispatch notes the vehicle has more speeding complaints than miles on it.");
            };
            IdentityEvents.VehicleAssigned += _onPlate;
        }

        /// <summary>A beat of dead air, then dispatch answers the lookup.</summary>
        private Task DispatchReply(string text) => Guard(nameof(DispatchReply), async () =>
        {
            await BaseScript.Delay(1500);
            if (Ended || IsCleanedUp || !Mabel.Status.IsAlive) return;
            RadioApi.Transmit(new RadioTransmission() { Text = text, Priority = RadioPriority.Routine });
        });

        private void Unhook()
        {
            if (_onCitation != null)
            {
                FilingEvents.CitationFiled -= _onCitation;
                _onCitation = null;
            }
            if (_onPlate != null)
            {
                IdentityEvents.VehicleAssigned -= _onPlate;
                _onPlate = null;
            }
        }

        /// <summary>Per-tick watch over the unresolved stop: warn input, idle pressure, and the bingo timer.</summary>
        private Task Watch() => Guard(nameof(Watch), async () =>
        {
            while (Live)
            {
                await BaseScript.Delay(TickMs);
                if (!Live || !Interactive) continue;

                var inCruiser = Game.PlayerPed.IsInVehicle();

                if (_dialogueDone && !inCruiser &&
                    Game.PlayerPed.Position.DistanceToSquared(Mabel.Position) <= WarnRange * WarnRange &&
                    ControlService.IsControlJustPressed(0, Control.Context))
                {
                    await Warn();
                    return;
                }

                if (_phase != Phase.Stopped) continue;

                _awayMs = inCruiser ? _awayMs + TickMs : 0;
                if (_awayMs >= AbandonMs)
                {
                    await Bolt();
                    continue;
                }

                _idleMs += TickMs;
                if (_idleMs >= IdlePressureMs && _pressureIndex < PressureLines.Length)
                    await Say(PressureLines[_pressureIndex++]);
            }
        });

        // ---- the hidden branch: she leaves -----------------------------------------------

        private async Task Bolt()
        {
            _phase = Phase.Bolting;
            _fled = true;

            RadioApi.Transmit(new RadioTransmission()
            {
                Text = "Dispatch, be advised — the Randal vehicle just left the stop. She's… headed for bingo. Again.",
                Priority = RadioPriority.Priority
            });

            // Flee() starts the FiveFR pursuit bookkeeping (blip, BOLO if identified); the drive itself is hers to botch.
            if (!Mabel.Flee())
                DiagnosticLog.WriteLine($"{Tag} Flee() could not start a pursuit; driving to the hall anyway.");
            await BaseScript.Delay(300);
            if (!Live) return;

            Mabel.Ped.Task.ClearAll();
            DriveToHall();
            Mabel.Blip.Show(BlipColor.Red, null, "Speeding Vehicle");

            // Predicate waits while true and gives up at the timeout.
            await QueueService.Predicate(() => Live && Vehicle.Position.DistanceToSquared(_HallLot) > 20f * 20f, 1000, 180000);
            if (!Live) return;

            Mabel.Ped.Task.ParkVehicle(Vehicle.Vehicle, _HallLot, HallLotHeading, 3f, false);
            await QueueService.Predicate(() => Live && Vehicle.Vehicle.Speed > 0.5f, TickMs, 20000);
            if (!Live) return;

            Mabel.Ped.Task.LeaveVehicle(Vehicle.Vehicle, LeaveVehicleFlags.None);
            await QueueService.Predicate(() => Live && Mabel.Ped.IsInVehicle(), TickMs, 10000);
            if (!Live) return;

            Mabel.Blip.Hide();
            _phase = Phase.Hall;
            _idleMs = 0;
            Mabel.Move.To(_HallDoor, true);

            await QueueService.Predicate(() => Live && Mabel.Position.DistanceToSquared(_HallDoor) > 2f * 2f, TickMs, 30000);
            if (Live) Mabel.Move.Hold();
        }

        private void DriveToHall()
        {
            var style = DrivingStyle.Rushed | DrivingStyle.IgnoreLights;
            Mabel.Ped.MaxDrivingSpeed = RushedSpeed + 6f;
            Mabel.Ped.DrivingSpeed = RushedSpeed;
            Mabel.Ped.DrivingStyle = style;
            Mabel.Ped.Task.DriveTo(Vehicle.Vehicle, _HallLot, 8f, RushedSpeed, (int)style);
        }

        // ---- resolutions -----------------------------------------------------------------

        /// <summary>Records the outcome (plus Fled if she bolted) as the storyline flag. Episode 2 reads it.</summary>
        private void Resolve(Ep1Outcome outcome)
        {
            _resolved = true;
            StorylineFlags.Increment(PlaysKey);
            StorylineFlags.Set(FlagKey, outcome | (_fled ? Ep1Outcome.Fled : Ep1Outcome.None));
        }

        private Task Warn() => Guard(nameof(Warn), async () =>
        {
            Resolve(Ep1Outcome.Warned);
            await Say(Repeat
                ? "Another one for the collection! Leadfoot Randal thanks you. James will never believe it."
                : "A warning for Leadfoot Randal! I'll put it with the others. I collect them, you know. James will never believe it.");

            if (Mabel.Ped.IsInVehicle())
            {
                // Exactly the speed limit. For one block.
                Mabel.Ped.MaxDrivingSpeed = SpeedLimit;
                Mabel.Ped.DrivingSpeed = SpeedLimit;
                Mabel.Ped.Task.CruiseWithVehicle(Vehicle.Vehicle, SpeedLimit, (int)DrivingStyle.Normal);
                await BaseScript.Delay(9000);
            }
            else
            {
                Mabel.Move.To(_HallDoor, false);
                await BaseScript.Delay(4000);
            }
            Finish();
        });

        /// <summary>Frisks fire every time. She's pesky and chatty about it, never hostile, and never repeats herself back to back.</summary>
        private Task OnFrisked(FriskedEventArgs e) => Guard(nameof(OnFrisked), async () =>
        {
            if (_friskTalking || Ended || IsCleanedUp || !Mabel.Status.IsAlive) return;
            _friskTalking = true;
            try
            {
                var count = _frisks++;
                string line = null;

                // The first frisk is about whatever the officer actually finds.
                if (count == 0 && e?.Items != null)
                {
                    if (e.Items.Any(i => i.Name.IndexOf("dauber", StringComparison.OrdinalIgnoreCase) >= 0))
                        line = "Mind the dauber, dear, it's lucky. It's got three full houses in it.";
                    else if (e.Items.Any(i => i.Name.IndexOf("glasses", StringComparison.OrdinalIgnoreCase) >= 0))
                        line = "Those are my reading glasses. Please don't put them on, you'll see how many numbers I miss.";
                    else if (e.Items.Any(i => i.Name.IndexOf("butterscotch", StringComparison.OrdinalIgnoreCase) >= 0))
                        line = "Careful with the butterscotches, dear. Those are emergency rations.";
                }
                if (line == null)
                    line = count < FriskLines.Length
                        ? FriskLines[count]
                        : null;
                if (line == null) return;

                await Say(Repeat && count == 0 ? "Oh, you again, with the hands." : line);
            }
            finally
            {
                _friskTalking = false;
            }
        });

        private Task OnCited() => Guard(nameof(OnCited), async () =>
        {
            Resolve(Ep1Outcome.Cited);
            await Say(Repeat
                ? "Another ticket for Leadfoot Randal! James is running out of wall. There. Framed by Friday. You still have lovely handwriting, officer."
                : "A ticket for Leadfoot Randal! James will never believe it. There. Framed by Friday. You have lovely handwriting, officer.");
            await BaseScript.Delay(2000);
            Finish();
        });

        private Task OnArrested() => Guard(nameof(OnArrested), async () =>
        {
            Resolve(Ep1Outcome.Arrested);
            Mabel.Surrender();
            await Say(Repeat
                ? "Well. Back again. Do they still not have bingo in jail? Asking for me. It's me."
                : "Well. Do they have bingo in jail? Asking for me. It's me.");
            await BaseScript.Delay(1500);
            await Say("Somebody ring my James. Actually, don't. He'll come down in the car.");
            await RideToJail();
        });

        /// <summary>She's in the cruiser (anything but her own Emperor) and still alive and un-booked.</summary>
        private bool RideActive => !Ended && !IsCleanedUp && !_booked && Mabel != null && Mabel.Status.IsAlive;

        private bool InPoliceVehicle() =>
            Mabel.Ped != null && Mabel.Ped.IsInVehicle() && Mabel.Ped.CurrentVehicle != Vehicle.Vehicle;

        /// <summary>Chatter in the back seat, only while the car is actually moving, until she's booked.</summary>
        private async Task RideToJail()
        {
            await QueueService.Predicate(() => RideActive && !InPoliceVehicle(), 500, 300000);
            if (!RideActive || !InPoliceVehicle()) return;

            await Say(Repeat
                ? "Ah, the back seat again. I'd like to say I'm used to it."
                : "Oh, a police car! I've always wanted to see the back seat. James says it's very exclusive.");

            var next = 0;
            var movingMs = 0;
            while (RideActive && next < RideLines.Length)
            {
                await BaseScript.Delay(1000);
                if (!InPoliceVehicle() || Mabel.Ped.CurrentVehicle.Speed < 1f) continue;

                movingMs += 1000;
                if (movingMs < RideLineGapMs) continue;
                movingMs = 0;
                await Say(RideLines[next++]);
            }
        }

        private Task OnBooked() => Guard(nameof(OnBooked), async () =>
        {
            _booked = true;
            RadioApi.TransmitAsOfficer("Dispatch, she says she's waiting for her bingo game.", "Copy. Advise the jail — subject is asking about bingo.");
            await BaseScript.Delay(4000);
            Finish();
        });

        /// <summary>She's the pack's comic relief; her death is not a punchline. No XP, no radio banter.</summary>
        private void OnKilled()
        {
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

    class WorldZone
    {
        private EWorldZoneCounty county;
        private static readonly IReadOnlyList<WorldZone> Zones = new List<WorldZone>() {
            #region Zones
            new WorldZone("AirP", "Los Santos International", EWorldZoneCounty.LosSantos),
            new WorldZone("Alamo", "Alamo Sea", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Alta", "Alta", EWorldZoneCounty.LosSantos),
            new WorldZone("ArmyB", "AFB Zancudo", EWorldZoneCounty.BlaineCounty),
            new WorldZone("BanhamCa", "Banham Canyon", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Banning", "Banning", EWorldZoneCounty.LosSantos),
            new WorldZone("Baytre", "Baytree Canyon", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Beach", "Vespucci Beach", EWorldZoneCounty.LosSantos),
            new WorldZone("BhamCa", "Banham Canyon", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("BradP", "Braddock Pass", EWorldZoneCounty.BlaineCounty),
            new WorldZone("BradT", "Braddock Tunnel", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Burton", "Burton", EWorldZoneCounty.LosSantos),
            new WorldZone("CANNY", "Raton Canyon", EWorldZoneCounty.BlaineCounty),
            new WorldZone("CCreak", "Cassidy Creek", EWorldZoneCounty.BlaineCounty),
            new WorldZone("CHIL", "Vinewood Hills", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("CHU", "Chumash", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("CMSW", "Chiliad Wilderness", EWorldZoneCounty.LosSantos),
            new WorldZone("CalafB", "Calafia Bridge", EWorldZoneCounty.BlaineCounty),
            new WorldZone("ChamH", "Chamberlain Hills", EWorldZoneCounty.LosSantos),
            new WorldZone("Cypre", "Cypress Flats", EWorldZoneCounty.LosSantos),
            new WorldZone("DTVine", "Downtown Vinewood", EWorldZoneCounty.LosSantos),
            new WorldZone("Davis", "Davis", EWorldZoneCounty.LosSantos),
            new WorldZone("DeLBe", "Del Perro Beach", EWorldZoneCounty.LosSantos),
            new WorldZone("DelPe", "Del Perro", EWorldZoneCounty.LosSantos),
            new WorldZone("DelSol", "Puerto Del Sol", EWorldZoneCounty.LosSantos),
            new WorldZone("Desrt", "Gd. Senora Desert", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Downt", "Downtown", EWorldZoneCounty.LosSantos),
            new WorldZone("EBuro", "El Burro Heights", EWorldZoneCounty.LosSantos),
            new WorldZone("ELGorL", "El Gordo Lighthouse", EWorldZoneCounty.BlaineCounty),
            new WorldZone("East_V", "East Vinewood", EWorldZoneCounty.LosSantos),
            new WorldZone("Elysian", "Elysian Island", EWorldZoneCounty.LosSantos),
            new WorldZone("Galfish", "Galilee", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Galli", "Galilep Park", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Golf", "GWC Golf Club", EWorldZoneCounty.LosSantos),
            new WorldZone("GrapeS", "Grapeseed", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Greatc", "Great Chaparral", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Harmo", "Harmony", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Hawick", "Hawick", EWorldZoneCounty.LosSantos),
            new WorldZone("Hors", "Vinewood Racetrack", EWorldZoneCounty.LosSantos),
            new WorldZone("HumLab", "Humane Labs", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Jail", "Bolingbroke Pen.", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Koreat", "Little Seoul", EWorldZoneCounty.LosSantos),
            new WorldZone("LAct", "Land Act Reservoir", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("LDam", "Land Act Dam", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("LMesa", "La Mesa", EWorldZoneCounty.LosSantos),
            new WorldZone("Lago", "Lago Zancudo", EWorldZoneCounty.BlaineCounty),
            new WorldZone("LegSqu", "Legion Square", EWorldZoneCounty.LosSantos),
            new WorldZone("LosPuer", "La Puerta", EWorldZoneCounty.LosSantos),
            new WorldZone("MTChil", "Mount Chiliad", EWorldZoneCounty.BlaineCounty),
            new WorldZone("MTGordo", "Mount Gordo", EWorldZoneCounty.BlaineCounty),
            new WorldZone("MTJose", "Mount Josiah", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Mirr", "Mirror Park", EWorldZoneCounty.LosSantos),
            new WorldZone("Morn", "Morningwood", EWorldZoneCounty.LosSantos),
            new WorldZone("Movie", "Backlot City", EWorldZoneCounty.LosSantos),
            new WorldZone("Murri", "Murrieta Heights", EWorldZoneCounty.LosSantos),
            new WorldZone("NCHU", "North Chumash", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Noose", "NOOSE HQ", EWorldZoneCounty.LosSantos),
            new WorldZone("Observ", "Galileo Observatory", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Oceana", "Pacific Ocean", EWorldZoneCounty.SanAndreas),
            new WorldZone("PBOX", "Pillbox Hill", EWorldZoneCounty.LosSantos),
            new WorldZone("PBluff", "Pacific Bluffs", EWorldZoneCounty.LosSantos),
            new WorldZone("PalCov", "Plaeto Cove", EWorldZoneCounty.BlaineCounty),
            new WorldZone("PalFor", "Paleto Forest", EWorldZoneCounty.BlaineCounty),
            new WorldZone("PalHigh", "Palomino Highlands", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Paleto", "Paleto Bay", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Palmpow", "Palmer-T. Pow. Sta.", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("ProcoB", "Procopio Beach", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Prol", "Ludendorff", EWorldZoneCounty.NorthYankton),
            new WorldZone("RANCHO", "Rancho", EWorldZoneCounty.LosSantos),
            new WorldZone("RGLEN", "Richman Glen", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("RTRAK", "Redwood Lights Track", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Richm", "Richman", EWorldZoneCounty.LosSantos),
            new WorldZone("Rockf", "Rockford Hills", EWorldZoneCounty.LosSantos),
            new WorldZone("SKID", "Mission Row", EWorldZoneCounty.LosSantos),
            new WorldZone("STRAW", "Strawberry", EWorldZoneCounty.LosSantos),
            new WorldZone("SanChia", "San Chiansky Mountains", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Sandy", "Sandy Shores", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Slab", "Stab City", EWorldZoneCounty.BlaineCounty),
            new WorldZone("Stad", "Maze Bank Arena", EWorldZoneCounty.LosSantos),
            new WorldZone("TEXTI", "Textile City", EWorldZoneCounty.LosSantos),
            new WorldZone("Tatamo", "Tataviam Mountains", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("Termina", "Terminal", EWorldZoneCounty.LosSantos),
            new WorldZone("TongVaH", "Tongava Hills", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("TongvaV", "Tongava Valley", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("VCana", "Vespucci Canals", EWorldZoneCounty.LosSantos),
            new WorldZone("Vesp", "Vespucci", EWorldZoneCounty.LosSantos),
            new WorldZone("Vine", "Vinewood", EWorldZoneCounty.LosSantos),
            new WorldZone("WVine", "West Vinewood", EWorldZoneCounty.LosSantos),
            new WorldZone("WindF", "Ron Alt. Wind Farm", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("ZP_ORT", "Port of South LS", EWorldZoneCounty.LosSantos),
            new WorldZone("Zancudo", "Zancudo River", EWorldZoneCounty.BlaineCounty),
            new WorldZone("zQ_UAR", "Davis Quartz Quarry", EWorldZoneCounty.LosSantosCounty),
            new WorldZone("SanAnd", "San Andreas", EWorldZoneCounty.SanAndreas)
            #endregion
        };

        private string GameName { get; }

        internal string RealAreaName { get; }

        internal string County
        {
            get => county.ToString();
        }

        private WorldZone(string gameName, string areaName, EWorldZoneCounty county)
        {
            GameName = gameName;
            RealAreaName = areaName;
            this.county = county;
        }

        internal static WorldZone GetZoneAtPosition(Vector3 position)
        {
            string zoneName = World.GetZoneDisplayName(position);
            string zoneNameLower = zoneName.ToLower();

            WorldZone worldZone = Zones.FirstOrDefault(zone => zone.GameName.ToLower() == zoneNameLower);

            if (worldZone == null) return new WorldZone(zoneName, zoneName, EWorldZoneCounty.SanAndreas);

            if (zoneNameLower == "sanand" && position.X > 3000.0 && position.Y < -4000.0) return new WorldZone("NorthYankton", "North Yankton", EWorldZoneCounty.NorthYankton);

            return worldZone;
        }

        public enum EWorldZoneCounty
        {
            LosSantos,
            LosSantosCounty,
            BlaineCounty,
            SanAndreas,
            NorthYankton
        }
    }
}
