﻿/*
 * This file is part of Project Hybrasyl.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the Affero General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but
 * without ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
 * or FITNESS FOR A PARTICULAR PURPOSE. See the Affero General Public License
 * for more details.
 *
 * You should have received a copy of the Affero General Public License along
 * with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * (C) 2020 ERISCO, LLC 
 *
 * For contributors and individual authors please refer to CONTRIBUTORS.MD.
 * 
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Transactions;
using Hybrasyl.Enums;
using Hybrasyl.Scripting;

namespace Hybrasyl.Objects
{
    public class Monster : Creature, ICloneable
    {
        protected static Random Rng = new Random();

        private bool _idle = true;

        private uint _mTarget;

        private Xml.Spawn _spawn;

        private uint _simpleDamage => Convert.ToUInt32(Rng.Next(_spawn.Damage.Min, _spawn.Damage.Max) * _variance);

        private Xml.CastableGroup _castables;
        private double _variance;

        public int ActionDelay = 800;

        public DateTime LastAction { get; set; }
        public bool IsHostile { get; set; }
        public bool ShouldWander { get; set; }
        public bool DeathDisabled => _spawn.Flags.HasFlag(Xml.SpawnFlags.DeathDisabled);
        public bool MovementDisabled => _spawn.Flags.HasFlag(Xml.SpawnFlags.MovementDisabled);
        public bool AiDisabled => _spawn.Flags.HasFlag(Xml.SpawnFlags.AiDisabled);

        public bool ScriptExists { get; set; }

        public Dictionary<uint, double> AggroTable { get; set; }
        public Xml.CastableGroup Castables => _castables;

        public bool HasCastNearDeath = false;
        
        
        public bool CanCast {
            get
            {
                //if any of these are present, return true.
                if (_spawn.Castables.Offense.Count > 0 || _spawn.Castables.Defense.Count > 0 || _spawn.Castables.NearDeath.Castables.Count > 0 || _spawn.Castables.OnDeath.Count > 0)
                {
                    return true;
                }
                return false;
            }
        } 

        public override void OnDeath()
        {
            if (DeathDisabled)
            {
                Stats.Hp = Stats.MaximumHp;               
                return;
            }

            Condition.Alive = false;
            var hitter = LastHitter as User;
            if (hitter == null)
            {
                Map.Remove(this);
                World.Remove(this);
                return; // Don't handle cases of MOB ON MOB COMBAT just yet
            }

            var deadTime = DateTime.Now;

            if (hitter.Grouped)
            {
                ItemDropAllowedLooters = hitter.Group.Members.Select(user => user.Name).ToList();
                hitter.Group.Members.ForEach(x => x.TrackKill(Name, deadTime));
            }
            else
            {
                ItemDropAllowedLooters.Add(hitter.Name);
                hitter.TrackKill(Name, deadTime);
            }

            hitter.ShareExperience(LootableXP);
            var itemDropTime = DateTime.Now;

            foreach (var itemname in LootableItems)
            {
                var item = Game.World.CreateItem(itemname);
                if (item == null)
                {
                    GameLog.UserActivityError("User {player}: looting {monster}, loot item {item} is missing", hitter.Name, Name, itemname);
                    continue;
                }
                item.ItemDropType = ItemDropType.MonsterLootPile;
                item.ItemDropAllowedLooters = ItemDropAllowedLooters;
                item.ItemDropTime = itemDropTime;
                World.Insert(item);
                Map.Insert(item, X, Y);
            }

            if (LootableGold > 0)
            {
                var golds = new Gold(LootableGold);
                golds.ItemDropType = ItemDropType.MonsterLootPile;
                golds.ItemDropAllowedLooters = ItemDropAllowedLooters;
                golds.ItemDropTime = itemDropTime;
                World.Insert(golds);
                Map.Insert(golds, X, Y);
            }
            Map.Remove(this);
            World.Remove(this);

        }

        // We follow a different pattern here due to the fact that monsters
        // are not intended to be long-lived objects, and we don't want to 
        // spend a lot of overhead and resources creating a full script (eg via
        // OnSpawn) when not needed 99% of the time.
        private void InitScript()
        {
            if (Script != null || !ScriptExists)               
                return;

            if (World.ScriptProcessor.TryGetScript(Name, out Script damageScript))
            {
                Script = damageScript;
                Script.AssociateScriptWithObject(this);
                ScriptExists = true;
            }
            else
                ScriptExists = false;                
        }

        public override void OnHear(VisibleObject speaker, string text, bool shout = false)
        {
            if (speaker == this)
                return;

            // FIXME: in the glorious future, run asynchronously with locking
            InitScript();
            if (Script != null)
            {
                Script.SetGlobalValue("text", text);
                Script.SetGlobalValue("shout", shout);

                if (speaker is User user)
                    Script.ExecuteFunction("OnHear", new HybrasylUser(user));
                else
                    Script.ExecuteFunction("OnHear", new HybrasylWorldObject(speaker));
            }
        }

        public override void OnDamage(Creature attacker, uint damage)
        {
            if (!AggroTable.ContainsKey(attacker.Id))
            {
                AggroTable.Add(attacker.Id, damage);
            }
            else
            {
                AggroTable[attacker.Id] += damage;
            }

            // FIXME: in the glorious future, run asynchronously with locking
            InitScript();

            if (Script != null)
            {
                Script.SetGlobalValue("damage", damage);
                Script.ExecuteFunction("OnDamage", this, attacker);
            }
        }

        public override void OnHeal(Creature healer, uint heal)
        {
            // FIXME: in the glorious future, run asynchronously with locking
            InitScript();
            if (Script != null)
            {
                Script.SetGlobalValue("heal", heal);
                Script.ExecuteFunction("OnHeal", this, healer);
            }
        }


        /// <summary>
        /// Calculates a sanity-checked stat using a spawn's variance value.
        /// </summary>
        /// <param name="stat">byte stat to be modified</param>
        /// <returns>new byte stat, +/- variance</returns>
        public byte CalculateVariance(byte stat)
        {
            var newStat = (int)Math.Round(stat + (stat * _variance));
            if (newStat > byte.MaxValue)
                return byte.MaxValue;
            else if (newStat < byte.MinValue)
                return byte.MinValue;

            return (byte)newStat;
        }

        /// <summary>
        /// Calculates a sanity-checked stat using a spawn's variance value.
        /// </summary>
        /// <param name="stat">uint stat to be modified</param>
        /// <returns>new uint stat, +/- variance</returns>
        public uint CalculateVariance(uint stat)
        {

            var newStat = (Int64)Math.Round(stat + (stat * _variance));
            if (newStat > uint.MaxValue)
                return uint.MaxValue;
            else if (newStat < uint.MinValue)
                return uint.MinValue;

            return (uint)newStat;
        }

        // Convenience methods to avoid calling CalculateVariance directly
        public byte VariantStr => CalculateVariance(_spawn.Stats.Str);
        public byte VariantInt => CalculateVariance(_spawn.Stats.Int);
        public byte VariantDex => CalculateVariance(_spawn.Stats.Dex);
        public byte VariantCon => CalculateVariance(_spawn.Stats.Con);
        public byte VariantWis => CalculateVariance(_spawn.Stats.Wis);
        public uint VariantHp => CalculateVariance(_spawn.Stats.Hp);
        public uint VariantMp => CalculateVariance(_spawn.Stats.Mp);

        private Loot _loot;

        public uint LootableXP => _loot?.Xp ?? 0 ;

        public uint LootableGold => _loot?.Gold ?? 0 ;

        public List<string> LootableItems => _loot?.Items ?? new List<string>();

        public Monster(Xml.Creature creature, Xml.Spawn spawn, int map, Loot loot = null)
        {

            _spawn = spawn;
            var buffed = Rng.Next() > 50;
            if (buffed)
                _variance = (Rng.NextDouble() * _spawn.Variance) + 1;
            else
                _variance = 1 - (Rng.NextDouble() * _spawn.Variance);


            Name = creature.Name;
            Sprite = creature.Sprite;
            World = Game.World;
            Map = Game.World.WorldData.Get<Map>(map);
            Stats.Level = spawn.Stats.Level;
            Stats.BaseHp = VariantHp;
            Stats.Hp = VariantHp;
            Stats.BaseMp = VariantMp;
            Stats.Mp = VariantMp;
            DisplayText = creature.Description;
            Stats.BaseStr = VariantStr;
            Stats.BaseInt = VariantInt;
            Stats.BaseWis = VariantWis;
            Stats.BaseCon = VariantCon;
            Stats.BaseDex = VariantDex;
            _castables = spawn.Castables;

            Stats.BaseDefensiveElement = spawn.GetDefensiveElement();
            Stats.BaseDefensiveElement = spawn.GetOffensiveElement();

            _loot = loot;

            if (spawn.Flags.HasFlag(Xml.SpawnFlags.AiDisabled))
                IsHostile = false;
            else
                IsHostile = _random.Next(0, 7) < 2;

            if (spawn.Flags.HasFlag(Xml.SpawnFlags.MovementDisabled))
                ShouldWander = false;
            else
                ShouldWander = IsHostile == false;

            AggroTable = new Dictionary<uint, double>();
        }

        public Creature Target
        {
            get
            {
                return World.Objects.ContainsKey(_mTarget) ? (Creature)World.Objects[_mTarget] : null;
            }
            set
            {
                _mTarget = value?.Id ?? 0;
            }
        }

        public override int GetHashCode()
        {
            return (Name.GetHashCode() * Id.GetHashCode()) - 1;
        }

        public virtual bool Pathfind(byte x, byte y)
        {
            var xDelta = Math.Abs(x - X);
            var yDelta = Math.Abs(y - Y);

            if (xDelta > yDelta)
            {
                Walk(x > X ? Xml.Direction.East : Xml.Direction.West);
            }

            return false;
        }

        public bool CheckFacing(Xml.Direction direction, Creature target)
        {
            if (Math.Abs(this.X - target.X) <= 1 && Math.Abs(this.Y - target.Y) <= 1)
            {
                if (((this.X - target.X) == 1 && (this.Y - target.Y) == 0))
                {
                    //check if facing west
                    if (this.Direction == Xml.Direction.West) return true;
                    else
                    {
                        this.Turn(Xml.Direction.West);
                    }
                }
                if (((this.X - target.X) == -1 && (this.Y - target.Y) == 0))
                {
                    //check if facing east
                    if (this.Direction == Xml.Direction.East) return true;
                    else
                    {
                        this.Turn(Xml.Direction.East);
                    }
                }
                if (((this.X - target.X) == 0 && (this.Y - target.Y) == 1))
                {
                    //check if facing south
                    if (this.Direction == Xml.Direction.North) return true;
                    else
                    {
                        this.Turn(Xml.Direction.North);
                    }
                }
                if (((this.X - target.X) == 0 && (this.Y - target.Y) == -1))
                {
                    if (this.Direction == Xml.Direction.South) return true;
                    else
                    {
                        this.Turn(Xml.Direction.South);
                    }
                }
            }
            return false;
        }

        public void Cast(Creature aggroTarget, UserGroup targetGroup)
        {
            if (CanCast)
            {
                //need to determine what it should do, and what is available to it.

                var currentHpPercent = (double)(Stats.Hp / Stats.MaximumHp);

                if (currentHpPercent < 1)
                {
                    var selectedCastable = SelectSpawnCastable(SpawnCastType.OnDeath);

                    if (selectedCastable.Target == Xml.TargetType.Attacker)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(aggroTarget, selectedCastable);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }
                    }

                    if (selectedCastable.Target == Xml.TargetType.Group || selectedCastable.Target == Xml.TargetType.Random)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(targetGroup, selectedCastable, selectedCastable.Target);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }
                    }
                }

                if (currentHpPercent <= Castables.NearDeath.HealthPercent && HasCastNearDeath == false)
                {
                    HasCastNearDeath = true;

                    var selectedCastable = SelectSpawnCastable(SpawnCastType.NearDeath);

                    if (selectedCastable.Target == Xml.TargetType.Attacker)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(aggroTarget, selectedCastable);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }
                    }

                    if (selectedCastable.Target == Xml.TargetType.Group || selectedCastable.Target == Xml.TargetType.Random)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(targetGroup, selectedCastable, selectedCastable.Target);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }
                    }
                }

                var nextChoice = _random.Next(0, 1);

                if (nextChoice == 0) //offense
                {
                    var selectedCastable = SelectSpawnCastable(SpawnCastType.Offensive);

                    if (selectedCastable.Target == Xml.TargetType.Attacker)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(aggroTarget, selectedCastable);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }
                    }

                    if (selectedCastable.Target == Xml.TargetType.Group || selectedCastable.Target == Xml.TargetType.Random)
                    {
                        if (selectedCastable.LastCast.AddSeconds(selectedCastable.Interval) < DateTime.Now)
                        {
                            Cast(targetGroup, selectedCastable, selectedCastable.Target);
                            selectedCastable.LastCast = DateTime.Now;
                        }
                        else
                        {
                            AssailAttack(Direction, aggroTarget);
                        }

                    }
                }

                if (nextChoice == 1) //defense
                {
                    //not sure how to handle this one
                }
            }
        }

        public void Cast(Creature target, Xml.SpawnCastable creatureCastable)
        {
            var castable = World.WorldData.GetByIndex<Xml.Castable>(creatureCastable.Name);
            if (target is Merchant) return;
            UseCastable(castable, creatureCastable, target);
            Condition.Casting = false;
        }

        public void Cast(UserGroup target, Xml.SpawnCastable creatureCastable, Xml.TargetType targetType)
        {
            var castable = World.WorldData.GetByIndex<Xml.Castable>(creatureCastable.Name);

            if (targetType == Xml.TargetType.Group)
            {
                foreach(var user in target.Members)
                {
                    UseCastable(castable, creatureCastable, user);
                }
            }

            if(targetType == Xml.TargetType.Random)
            {
                var rngSelection = _random.Next(0, target.Count - 1);

                var user = target.Members[rngSelection];

                UseCastable(castable, creatureCastable, user);
            }

            Condition.Casting = false;
        }

        public Xml.SpawnCastable SelectSpawnCastable(SpawnCastType castType)
        {
            var nextSpell = 0;
            Xml.SpawnCastable creatureCastable = null;
            switch (castType)
            {
                case SpawnCastType.Offensive:
                    nextSpell = _random.Next(0, _castables.Offense.Count - 1);
                    creatureCastable = _castables.Offense[nextSpell];
                    break;
                case SpawnCastType.Defensive:
                    nextSpell = _random.Next(0, _castables.Defense.Count - 1);
                    creatureCastable = _castables.Defense[nextSpell];
                    break;
                case SpawnCastType.NearDeath:
                    nextSpell = _random.Next(0, _castables.NearDeath.Castables.Count - 1);
                    creatureCastable = _castables.NearDeath.Castables[nextSpell];
                    break;
                case SpawnCastType.OnDeath:
                    nextSpell = _random.Next(0, _castables.OnDeath.Count - 1);
                    creatureCastable = _castables.OnDeath[nextSpell];
                    break;
            }

            return creatureCastable;
        }



        public override List<Creature> GetTargets(Xml.Castable castable, Creature target = null)
        {
            IEnumerable<Creature> actualTargets = new List<Creature>();
            var intents = castable.Intents;
            List<VisibleObject> possibleTargets = new List<VisibleObject>();
            Creature origin;

            foreach (var intent in intents)
            {
                if (intent.IsShapeless)
                {
                    GameLog.UserActivityInfo("GetTarget: Shapeless");
                    // No shapes specified. 
                    // If UseType=Target, exact clicked target.
                    // If UseType=NoTarget Target=Self, caster.
                    // If UseType=NoTarget Target=Group, *entire* group regardless of location on map.
                    // Otherwise, no target.
                    if (intent.UseType == Xml.SpellUseType.Target)
                    {
                        // Exact clicked target
                        possibleTargets.Add(target);
                        GameLog.UserActivityInfo("GetTarget: exact clicked target");
                    }
                    else if (intent.UseType != Xml.SpellUseType.NoTarget)
                        GameLog.UserActivityWarning($"Unhandled intent type {intent.UseType}, ignoring");
                }

                if (intent.Map != null)
                {
                    // add entire map
                    GameLog.UserActivityInfo("GetTarget: adding map targets");
                    possibleTargets.AddRange(Map.EntityTree.GetAllObjects().Where(e => e is Creature));
                }

                if (intent.UseType == Xml.SpellUseType.NoTarget)
                {
                    origin = this;
                    GameLog.UserActivityInfo($"GetTarget: origin is {this.Name} at {this.X}, {this.Y}");

                }
                else
                {
                    GameLog.UserActivityInfo($"GetTarget: origin is {target.Name} at {target.X}, {target.Y}");
                    origin = target;
                }

                // Handle shapes
                foreach (var cross in intent.Cross)
                {
                    // Process cross targets
                    foreach (Xml.Direction direction in Enum.GetValues(typeof(Xml.Direction)))
                    {
                        GameLog.UserActivityInfo($"GetTarget: cross, {direction}, origin {origin.Name}, radius {cross.Radius}");
                        possibleTargets.AddRange(origin.GetDirectionalTargets(direction, cross.Radius));
                    }
                    // Add origin and let flags sort it out
                    possibleTargets.Add(origin);
                }
                foreach (var line in intent.Line)
                {
                    // Process line targets
                    GameLog.UserActivityInfo($"GetTarget: line, {line.Direction}, origin {origin.Name}, length {line.Length}");
                    possibleTargets.AddRange(origin.GetDirectionalTargets(origin.GetIntentDirection(line.Direction), line.Length));
                    // Similar to above, add origin
                    possibleTargets.Add(origin);
                }
                foreach (var square in intent.Square)
                {
                    // Process square targets
                    var r = (square.Side - 1) / 2;
                    var rect = new Rectangle(origin.X - r, origin.Y - r, square.Side, square.Side);
                    GameLog.UserActivityInfo($"GetTarget: square, {origin.X - r}, {origin.Y - r} - origin {origin.Name}, side length {square.Side}");
                    possibleTargets.AddRange(origin.Map.EntityTree.GetObjects(rect).Where(e => e is Creature));
                }
                foreach (var tile in intent.Tile)
                {
                    // Process tile targets, which can have either direction OR relative x/y
                    if (tile.Direction == Xml.IntentDirection.None)
                    {
                        if (tile.RelativeX == 0 && tile.RelativeY == 0)
                        {
                            GameLog.UserActivityInfo($"GetTarget: tile, origin {origin.Name}, RelativeX && RelativeY == 0, skipping");
                            continue;
                        }
                        else
                        {
                            GameLog.UserActivityInfo($"GetTarget: tile, ({origin.X + tile.RelativeX}, {origin.Y + tile.RelativeY}, origin {origin.Name}");
                            possibleTargets.AddRange(origin.Map.GetTileContents(origin.X + tile.RelativeX, origin.Y + tile.RelativeY).Where(e => e is Creature));
                        }
                    }
                    else
                    {
                        GameLog.UserActivityInfo($"GetTarget: tile, intent {tile.Direction}, direction {origin.GetIntentDirection(tile.Direction)}, origin {origin.Name}");
                        possibleTargets.Add(origin.GetDirectionalTarget(origin.GetIntentDirection(tile.Direction)));
                    }

                }
                List<Creature> possible = intent.MaxTargets > 0 ? possibleTargets.Take(intent.MaxTargets).OfType<Creature>().ToList() : possibleTargets.OfType<Creature>().ToList();
                if (possible != null && possible.Count > 0)
                    actualTargets = actualTargets.Concat(possible);
                else GameLog.UserActivityInfo("GetTarget: No targets found");

                // Remove all merchants
                // TODO: perhaps improve with a flag or extend in the future
                actualTargets = actualTargets.SkipWhile(e => e is Merchant);

                // Process intent flags

                var this_id = this.Id;

                if (this is Monster)
                {
                    // No hostile flag: remove players
                    if (!intent.Flags.Contains(Xml.IntentFlags.Hostile))
                    {
                        actualTargets = actualTargets.SkipWhile(e => e is Monster);
                    }
                    // No friendly flag: remove monsters
                    if (!intent.Flags.Contains(Xml.IntentFlags.Friendly))
                    {
                        actualTargets = actualTargets.SkipWhile(e => e is User);
                    }
                    // Group / pvp: n/a
                }

                // No Self flag: remove self 
                if (!intent.Flags.Contains(Xml.IntentFlags.Self))
                {
                    GameLog.UserActivityInfo($"Trying to remove self: my id is {this.Id} and actualtargets contains {String.Join(',', actualTargets.Select(e => e.Id).ToList())}");
                    actualTargets = actualTargets.Where(e => e.Id != this_id);
                    GameLog.UserActivityInfo($"did it happen :o -  my id is {this.Id} and actualtargets contains {String.Join(',', actualTargets.Select(e => e.Id).ToList())}");
                }
            }
            return actualTargets.ToList();
        }

        public bool UseCastable(Xml.Castable castObject, Xml.SpawnCastable spawnCastable, Creature target = null)
        {
            if (!Condition.CastingAllowed) return false;

            var damage = _random.Next(spawnCastable.MinDmg, spawnCastable.MaxDmg);
            List<Creature> targets;

            targets = GetTargets(castObject, target);

            if (targets.Count() == 0 && castObject.IsAssail == false) return false;

            // We do these next steps to ensure effects are displayed uniformly and as fast as possible
            var deadMobs = new List<Creature>();
            if (castObject.Effects?.Animations?.OnCast != null)
            {


                foreach (var tar in targets)
                {
                    foreach (var user in tar.viewportUsers)
                    {
                        user.SendEffect(tar.Id, castObject.Effects.Animations.OnCast.Target.Id, castObject.Effects.Animations.OnCast.Target.Speed);
                    }
                }
                if (castObject.Effects?.Animations?.OnCast?.SpellEffect != null)
                    Effect(castObject.Effects.Animations.OnCast.SpellEffect.Id, castObject.Effects.Animations.OnCast.SpellEffect.Speed);
            }

            if (castObject.Effects?.Sound != null)
                PlaySound(castObject.Effects.Sound.Id);

            GameLog.UserActivityInfo($"UseCastable: {Name} casting {castObject.Name}, {targets.Count()} targets");

            if (!string.IsNullOrEmpty(castObject.Script))
            {
                // If a script is defined we fire it immediately, and let it handle targeting / etc
                if (Game.World.ScriptProcessor.TryGetScript(castObject.Script, out Script script))
                    return script.ExecuteFunction("OnUse", this);
                else
                {
                    GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name}: castable script {castObject.Script} missing");
                    return false;
                }

            }

            foreach (var tar in targets)
            {
                if (castObject.Effects?.ScriptOverride == true)
                {
                    // TODO: handle castables with scripting
                    // DoStuff();
                    continue;
                }
                if (!castObject.Effects.Damage.IsEmpty)
                {
                    Xml.Element attackElement;
                    var damageOutput = NumberCruncher.CalculateDamage(castObject, tar, this);
                    if (castObject.Element == Xml.Element.Random)
                    {
                        Random rnd = new Random();
                        var Elements = Enum.GetValues(typeof(Xml.Element));
                        attackElement = (Xml.Element)Elements.GetValue(rnd.Next(Elements.Length));
                    }
                    else if (castObject.Element != Xml.Element.None)
                        attackElement = castObject.Element;
                    else
                        attackElement = (Stats.OffensiveElementOverride != Xml.Element.None ? Stats.OffensiveElementOverride : Stats.BaseOffensiveElement);

                    tar.Damage(damageOutput.Amount, attackElement, damageOutput.Type, damageOutput.Flags, this, false);

                    if (tar.Stats.Hp <= 0) { deadMobs.Add(tar); }
                }
                // Note that we ignore castables with both damage and healing effects present - one or the other.
                // A future improvement might be to allow more complex effects.
                else if (!castObject.Effects.Heal.IsEmpty)
                {
                    var healOutput = NumberCruncher.CalculateHeal(castObject, tar, this);
                    tar.Heal(healOutput, this);
                }

                // Handle statuses

                foreach (var status in castObject.Effects.Statuses.Add.Where(e => e.Value != null))
                {
                    Xml.Status applyStatus;
                    if (World.WorldData.TryGetValueByIndex<Xml.Status>(status.Value, out applyStatus))
                    {
                        GameLog.UserActivityInfo($"UseCastable: {Name} casting {castObject.Name} - applying status {status.Value}");
                        ApplyStatus(new CreatureStatus(applyStatus, tar, castObject));
                    }
                    else
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - failed to add status {status.Value}, does not exist!");
                }

                foreach (var status in castObject.Effects.Statuses.Remove)
                {
                    Xml.Status applyStatus;
                    if (World.WorldData.TryGetValueByIndex<Xml.Status>(status, out applyStatus))
                    {
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - removing status {status}");
                        RemoveStatus(applyStatus.Icon);
                    }
                    else
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - failed to remove status {status}, does not exist!");

                }
            }
            // Now flood away
            foreach (var dead in deadMobs)
                World.ControlMessageQueue.Add(new HybrasylControlMessage(ControlOpcodes.HandleDeath, dead));
            Condition.Casting = false;
            return true;
        }

        public bool UseCastable(Xml.Castable castObject, Xml.SpawnCastable spawnCastable, UserGroup target = null)
        {
            if (!Condition.CastingAllowed) return false;

            var damage = _random.Next(spawnCastable.MinDmg, spawnCastable.MaxDmg);
            List<Creature> targets = new List<Creature>();


            foreach(var user in target.Members)
            {
                var tars = GetTargets(castObject, user);
                targets.AddRange(tars);
            }

            

            if (targets.Count() == 0 && castObject.IsAssail == false) return false;

            // We do these next steps to ensure effects are displayed uniformly and as fast as possible
            var deadMobs = new List<Creature>();
            if (castObject.Effects?.Animations?.OnCast != null)
            {


                foreach (var tar in targets)
                {
                    foreach (var user in tar.viewportUsers)
                    {
                        user.SendEffect(tar.Id, castObject.Effects.Animations.OnCast.Target.Id, castObject.Effects.Animations.OnCast.Target.Speed);
                    }
                }
                if (castObject.Effects?.Animations?.OnCast?.SpellEffect != null)
                    Effect(castObject.Effects.Animations.OnCast.SpellEffect.Id, castObject.Effects.Animations.OnCast.SpellEffect.Speed);
            }

            if (castObject.Effects?.Sound != null)
                PlaySound(castObject.Effects.Sound.Id);

            GameLog.UserActivityInfo($"UseCastable: {Name} casting {castObject.Name}, {targets.Count()} targets");

            if (!string.IsNullOrEmpty(castObject.Script))
            {
                // If a script is defined we fire it immediately, and let it handle targeting / etc
                if (Game.World.ScriptProcessor.TryGetScript(castObject.Script, out Script script))
                    return script.ExecuteFunction("OnUse", this);
                else
                {
                    GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name}: castable script {castObject.Script} missing");
                    return false;
                }

            }

            foreach (var tar in targets)
            {
                if (castObject.Effects?.ScriptOverride == true)
                {
                    // TODO: handle castables with scripting
                    // DoStuff();
                    continue;
                }
                if (!castObject.Effects.Damage.IsEmpty)
                {
                    Xml.Element attackElement;
                    var damageOutput = NumberCruncher.CalculateDamage(castObject, tar, this);
                    if (castObject.Element == Xml.Element.Random)
                    {
                        Random rnd = new Random();
                        var Elements = Enum.GetValues(typeof(Xml.Element));
                        attackElement = (Xml.Element)Elements.GetValue(rnd.Next(Elements.Length));
                    }
                    else if (castObject.Element != Xml.Element.None)
                        attackElement = castObject.Element;
                    else
                        attackElement = (Stats.OffensiveElementOverride != Xml.Element.None ? Stats.OffensiveElementOverride : Stats.BaseOffensiveElement);

                    tar.Damage(damageOutput.Amount, attackElement, damageOutput.Type, damageOutput.Flags, this, false);

                    if (tar.Stats.Hp <= 0) { deadMobs.Add(tar); }
                }
                // Note that we ignore castables with both damage and healing effects present - one or the other.
                // A future improvement might be to allow more complex effects.
                else if (!castObject.Effects.Heal.IsEmpty)
                {
                    var healOutput = NumberCruncher.CalculateHeal(castObject, tar, this);
                    tar.Heal(healOutput, this);
                }

                // Handle statuses

                foreach (var status in castObject.Effects.Statuses.Add.Where(e => e.Value != null))
                {
                    Xml.Status applyStatus;
                    if (World.WorldData.TryGetValueByIndex<Xml.Status>(status.Value, out applyStatus))
                    {
                        GameLog.UserActivityInfo($"UseCastable: {Name} casting {castObject.Name} - applying status {status.Value}");
                        ApplyStatus(new CreatureStatus(applyStatus, tar, castObject));
                    }
                    else
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - failed to add status {status.Value}, does not exist!");
                }

                foreach (var status in castObject.Effects.Statuses.Remove)
                {
                    Xml.Status applyStatus;
                    if (World.WorldData.TryGetValueByIndex<Xml.Status>(status, out applyStatus))
                    {
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - removing status {status}");
                        RemoveStatus(applyStatus.Icon);
                    }
                    else
                        GameLog.UserActivityError($"UseCastable: {Name} casting {castObject.Name} - failed to remove status {status}, does not exist!");

                }
            }
            // Now flood away
            foreach (var dead in deadMobs)
                World.ControlMessageQueue.Add(new HybrasylControlMessage(ControlOpcodes.HandleDeath, dead));
            Condition.Casting = false;
            return true;
        }

        public void AssailAttack(Xml.Direction direction, Creature target = null)
        {
            if (target == null)
            {
                var obj = GetDirectionalTarget(direction);
                var monster = obj as Monster;
                if (monster != null) target = monster;
                var user = obj as User;
                if (user != null)
                {
                    target = user;
                }
                var npc = obj as Merchant;
                if (npc != null)
                {
                    target = npc;
                }
                //try to get the creature we're facing and set it as the target.
            }
            
            // A monster's assail is just a straight attack, no skills involved.
            SimpleAttack(target);
                            
            //animation handled here as to not repeatedly send assails.
            var assail = new ServerPacketStructures.PlayerAnimation() { Animation = 1, Speed = 20, UserId = this.Id };
            //Enqueue(assail.Packet());
            //Enqueue(sound.Packet());
            SendAnimation(assail.Packet());
            PlaySound(1);
        }

        /// <summary>
        /// A simple directional attack by a monster (equivalent of straight assail).
        /// </summary>
        /// <param name="direction"></param>
        /// <param name="target"></param>
        public void SimpleAttack(Creature target) => target?.Damage(_simpleDamage, Stats.BaseOffensiveElement, Xml.DamageType.Physical, Xml.DamageFlags.None, this);

        public override void ShowTo(VisibleObject obj)
        {
            if (!(obj is User)) return;
            var user = obj as User;
            user.SendVisibleCreature(this);
        }

        public bool IsIdle()
        {
            return _idle;
        }

        public void Awaken()
        {
            _idle = false;
            //add to alive monsters?
        }

        public void Sleep()
        {
            _idle = true;
            //return to idle state
        }

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }

}
