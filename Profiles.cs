using System;
using System.Collections.Generic;
using CitizenFX.Core;
using FiveFR._API;
using FiveFR._API.Classes;
using FiveFR._API.Entities;
using FiveFR.Interfaces;

namespace FiveFR.Storylines
{
    public abstract class Identity
    {
        public abstract string Name { get; }
        public abstract DateTime DOB { get; }
        public abstract PedHash Hash { get; }
    }
    public static class Profiles
    {
        public class MabelRandal : Identity
        {
            public override string Name { get; } =  "Mabel Randal";
            public override DateTime DOB { get; } = BirthdayForAge(79, 10, 21);
            public override PedHash Hash { get; } = PedHash.Genstreet01AFO;
            
            // Always 79 on the day she's stopped (birthday October 21st).
            private static DateTime BirthdayForAge(int age, int month, int day)
            {
                var today = DateTime.Today;
                var birthdayPassed = today >= new DateTime(today.Year, month, day);
                return new DateTime(today.Year - age - (birthdayPassed ? 0 : 1), month, day);
            }

            public VehicleHash VehicleHash { get; } = VehicleHash.Emperor;
            public VehicleColor VehicleColor { get; } = VehicleColor.MetallicCream;
            public string VehiclePlate { get; } = "MABEL79";
        }
    }
}