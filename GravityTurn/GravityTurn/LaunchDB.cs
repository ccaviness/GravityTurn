﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KSP.IO;
using System.IO;
using UnityEngine;


namespace GravityTurn
{
    class DBEntry : IEquatable<DBEntry>, IComparable<DBEntry>
    {
        [Persistent]
        public double StartSpeed;
        [Persistent]
        public double APTimeStart;
        [Persistent]
        public double APTimeFinish;
        [Persistent]
        public double TurnAngle;
        [Persistent]
        public double Sensitivity;
        [Persistent]
        public double Roll;
        [Persistent]
        public double DestinationHeight;
        [Persistent]
        public double PressureCutoff;
        [Persistent]
        public double TotalLoss;
        [Persistent]
        public double MaxHeat;
        [Persistent]
        public bool LaunchSuccess;

        public int BetterThan(DBEntry other)
        {
            if (IsHot() && !other.IsHot())
                return 1;
            if (other.IsHot() && !IsHot())
                return -1;
            if (other.IsHot() && other.MaxHeat > MaxHeat) // other overheated more than us
                return -1;
            if (!other.LaunchSuccess && LaunchSuccess) // other failed and we didn't
                return -1;
            if (other.TotalLoss == TotalLoss)
                return 0;
            return (other.TotalLoss > TotalLoss)?-1:1;
        }

        public int CompareTo(DBEntry other)
        {
            if (other == null)
                return 1;
            else
                return this.BetterThan(other);
        }

        public bool Equals(DBEntry other)
        {
            return other.StartSpeed == StartSpeed && other.TurnAngle == TurnAngle;
        }

        public bool Equals(GravityTurner turner)
        {
            return StartSpeed == turner.StartSpeed && TurnAngle == turner.TurnAngle && MaxHeat == turner.MaxHeat && TotalLoss == turner.TotalLoss;
        }

        public override string ToString()
        {
            return string.Format("{0:0.00}/{1:0.00}", TurnAngle, StartSpeed);
        }

        public bool IsHot()
        {
            return MaxHeat > 0.95;
        }

        public bool MoreAggressive(DBEntry other)
        {
            return TurnAngle / StartSpeed > other.TurnAngle / other.StartSpeed;
        }
    }

    class LaunchDB
    {
        GravityTurner turner;
        ConfigNode root = null;
        [Persistent]
        List<DBEntry> DB = new List<DBEntry>();

        public LaunchDB(GravityTurner inTurner)
        {
            turner = inTurner;
        }

        ///<summary>
        ///Get the least aggressive result from launches that overheated.
        ///</summary>
        public DBEntry LeastCritical()
        {
            DBEntry crit = null;
            foreach (DBEntry entry in DB)
            {
                if (entry.MaxHeat > 0.95 && (crit == null || (entry.TurnAngle < crit.TurnAngle && entry.StartSpeed > crit.StartSpeed)))
                {
                    crit = entry;
                }
            }
            return crit;
        }


        ///<summary>
        ///Find the launch setting that was too aggressive and became less efficient (if any).
        ///</summary>
        public DBEntry EfficiencyTippingPoint()
        {
            if (DB.Count() < 2)
                return null;
            double loss = 0;
            foreach (DBEntry entry in DB.OrderBy(o=>(o.TurnAngle/o.StartSpeed)))
            {
                if (entry.MaxHeat > 0.95) // Stop if we find one TOO aggressive
                    break;
                if (loss!=0 && entry.TotalLoss > loss)
                    // This item is less efficient than the previous
                    return entry;
                loss = entry.TotalLoss;
            }
            return null;  // We didn't find one
        }

        ///<summary>
        ///Just replay the best launch, no learning to be done.
        ///</summary>
        public bool BestSettings(out double TurnAngle, out double StartSpeed)
        {
            DB.Sort();
            TurnAngle = 0;
            StartSpeed = 0;
            if (DB.Count() < 1 || DB[0].MaxHeat >=1 || !DB[0].LaunchSuccess)
                return false;
            TurnAngle = DB[0].TurnAngle;
            StartSpeed = DB[0].StartSpeed;
            return true;
        }

        ///<summary>
        ///Do the real work to analyze previous results and get recommended settings.
        ///</summary>
        public bool GuessSettings(out double TurnAngle, out double StartSpeed)
        {
            // sort by most aggressive
            DB.Sort();
            TurnAngle = 0;
            StartSpeed = 0;
            if (DB.Count() == 0)
                return false;
            if (DB.Count() == 1)
            {
                GravityTurner.Log("Only one previous result");
                if (DB[0].MaxHeat < 0.90)
                {
                    float Adjust = Mathf.Clamp((float)DB[0].MaxHeat + (float)(1 - DB[0].MaxHeat) / 2, 0.8f, 0.95f);
                    TurnAngle = DB[0].TurnAngle / Adjust;
                    StartSpeed = DB[0].StartSpeed * Adjust;
                }
                else if (DB[0].MaxHeat > 0.95)
                {
                    TurnAngle = DB[0].TurnAngle * 0.95;
                    StartSpeed = DB[0].StartSpeed * 1.05;
                }
                else
                {
                    TurnAngle = DB[0].TurnAngle;
                    StartSpeed = DB[0].StartSpeed;
                }
                return true;
            }

            // more than one result, now we can do real work

            // Simple linear progression 2nd best -> best -> next

            TurnAngle = DB[0].TurnAngle + DB[0].TurnAngle - DB[1].TurnAngle;
            StartSpeed = DB[0].StartSpeed + DB[0].StartSpeed - DB[1].StartSpeed;


            // Check for overheated launches so we don't make that mistake again
            DBEntry hotrun = LeastCritical();
            if (hotrun != null && TurnAngle / StartSpeed >= hotrun.TurnAngle / hotrun.StartSpeed*0.99) // Close to a previous overheating run
            {
                TurnAngle = (DB[0].TurnAngle + hotrun.TurnAngle)/2;
                StartSpeed = (DB[0].StartSpeed + hotrun.StartSpeed)/2;
                GravityTurner.Log("Found hot run, set between {0} and {1}",
                    DB[0].ToString(),hotrun.ToString()
                    );
            }

            // Need to check to see if we're past the point of max efficiency
            DBEntry toomuch = EfficiencyTippingPoint();
            // If we're within 1% of a launch that was inefficient (or beyond)...
            if (toomuch != null && TurnAngle/StartSpeed >= toomuch.TurnAngle/toomuch.StartSpeed*0.99)
            {
                // Go halfway between the best and too much
                TurnAngle = (DB[0].TurnAngle + toomuch.TurnAngle) / 2;
                StartSpeed = (DB[0].StartSpeed + toomuch.StartSpeed) / 2;
            }

            return true;
        }

        ///<summary>
        ///Get or create a new DBEntry based on angle and speed, so we don't have duplicates.
        ///</summary>
        DBEntry GetEntry()
        {
            foreach (DBEntry entry in DB)
            {
                if (entry.TurnAngle == turner.TurnAngle && entry.StartSpeed == turner.StartSpeed && entry.DestinationHeight==turner.DestinationHeight)
                    return entry;
            }
            DBEntry newentry = new DBEntry();
            DB.Add(newentry);
            return newentry;
        }

        ///<summary>
        ///Update or create a new entry in the DB for the current launch.
        ///</summary>
        public void RecordLaunch()
        {
            foreach (DBEntry entry in DB)
            {
                if (entry.Equals(turner))
                    return;
            }

            DBEntry newentry = GetEntry();
            newentry.TurnAngle = turner.TurnAngle;
            newentry.StartSpeed = turner.StartSpeed;
            newentry.TotalLoss = turner.TotalLoss;
            newentry.MaxHeat = turner.MaxHeat;
            newentry.DestinationHeight = turner.DestinationHeight;
            newentry.LaunchSuccess = GravityTurner.getVessel.orbit.ApA >= newentry.DestinationHeight * 1000;
        }

        public string GetFilename()
        {
            return IOUtils.GetFilePathFor(turner.GetType(), string.Format("gt_launchdb_{0}_{1}.cfg", turner.LaunchName, turner.LaunchBody.name));
        }

        public void Load()
        {
            try
            {
                root = ConfigNode.Load(GetFilename());
                if (root != null)
                {
                    if (ConfigNode.LoadObjectFromConfig(this, root))
                        GravityTurner.Log("Vessel DB loaded from {0}", GetFilename());
                    else
                        GravityTurner.Log("Vessel DB NOT loaded from {0}", GetFilename());
                }
            }
            catch (Exception ex)
            {
                GravityTurner.Log("Vessel DB Load error {0}" ,ex.ToString());
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetFilename()));
            root = ConfigNode.CreateConfigFromObject(this);
            root.Save(GetFilename());
            GravityTurner.Log("Vessel DB saved to {0}", GetFilename());
        }
    }
}
