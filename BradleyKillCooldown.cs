/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("Bradley Kill Cooldown", "VisEntities", "1.0.0")]
    [Description("Adds a cooldown after killing a Bradley so the same players can't farm it nonstop.")]
    public class BradleyKillCooldown : RustPlugin
    {
        #region 3rd Party Dependencies

        [PluginReference]
        private readonly Plugin Clans, Friends;

        #endregion 3rd Party Dependencies

        #region Fields

        private static BradleyKillCooldown _plugin;
        private static Configuration _config;
        private readonly Dictionary<ulong, double> _cooldownUntil = new Dictionary<ulong, double>();

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Minutes Before Player Can Kill Another Bradley")]
            public double MinutesBeforePlayerCanKillAnotherBradley { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                MinutesBeforePlayerCanKillAnotherBradley = 60.0
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
        }

        private void Unload()
        {
            _config = null;
            _plugin = null;
        }

        private void OnEntityDeath(BradleyAPC bradley, HitInfo deathInfo)
        {
            if (bradley == null || deathInfo == null) return;

            BasePlayer killer = deathInfo.InitiatorPlayer;
            if (killer == null || PlayerUtil.IsNPC(killer))
                return;

            if (PermissionUtil.HasPermission(killer, PermissionUtil.BYPASS))
                return;

            ApplyCooldown(killer.userID);

            double totalSeconds = _config.MinutesBeforePlayerCanKillAnotherBradley * 60.0;
            MessagePlayer(killer, Lang.Notice_CooldownStarted, FormatDuration(totalSeconds));
        }

        private object OnEntityTakeDamage(BradleyAPC bradley, HitInfo hitInfo)
        {
            if (bradley == null || hitInfo == null)
                return null;

            BasePlayer attacker = hitInfo.InitiatorPlayer;
            if (attacker == null || PlayerUtil.IsNPC(attacker))
                return null;

            if (PermissionUtil.HasPermission(attacker, PermissionUtil.BYPASS))
                return null;

            if (IsAnyRelatedPlayerOnCooldown(attacker.userID, out double remaining))
            {
                hitInfo.damageTypes.ScaleAll(0f);
                MessagePlayer(attacker, Lang.Notice_OnCooldown, FormatDuration(remaining));
                return true;
            }

            return null;
        }

        #endregion Oxide Hooks

        #region Cooldown Logic

        private double Now()
        {
            return Interface.Oxide.Now;
        }

        private bool IsPlayerOnCooldown(ulong id, out double remaining)
        {
            remaining = 0;

            if (_cooldownUntil.TryGetValue(id, out double until))
            {
                double now = Now();
                if (until > now)
                {
                    remaining = until - now;
                    return true;
                }
                _cooldownUntil.Remove(id);
            }
            return false;
        }

        private bool IsAnyRelatedPlayerOnCooldown(ulong playerId, out double remainingSeconds)
        {
            remainingSeconds = 0;

            double rem;
            if (IsPlayerOnCooldown(playerId, out rem))
            {
                remainingSeconds = rem;
                return true;
            }

            RelationshipManager.PlayerTeam team = PlayerUtil.GetTeam(playerId);
            if (team != null)
            {
                if (team.members != null)
                {
                    foreach (ulong memberId in team.members)
                    {
                        if (memberId == playerId)
                            continue;

                        if (!PlayerUtil.AreTeammates(playerId, memberId)) 
                            continue;

                        if (IsPlayerOnCooldown(memberId, out rem))
                        {
                            remainingSeconds = rem;
                            return true;
                        }
                    }
                }
            }

            List<ulong> idsToCheck = new List<ulong>();

            string clanTag = ClansUtil.GetClanTag(playerId);
            if (!string.IsNullOrEmpty(clanTag))
            {
                JObject clanObj = ClansUtil.GetClan(clanTag);
                if (clanObj != null)
                {
                    JArray members = clanObj["members"] as JArray;
                    if (members != null)
                    {
                        foreach (JToken token in members)
                        {
                            ulong id;
                            if (ulong.TryParse(token.ToString(), out id))
                            {
                                if (id != playerId)
                                    idsToCheck.Add(id);
                            }
                        }
                    }

                    JArray allies = clanObj["alliances"] as JArray;
                    if (allies != null)
                    {
                        foreach (JToken allyTagToken in allies)
                        {
                            string allyTag = allyTagToken.ToString();
                            JObject allyClan = ClansUtil.GetClan(allyTag);
                            if (allyClan != null)
                            {
                                JArray allyMembers = allyClan["members"] as JArray;
                                if (allyMembers != null)
                                {
                                    foreach (JToken token in allyMembers)
                                    {
                                        ulong id;
                                        if (ulong.TryParse(token.ToString(), out id))
                                        {
                                            if (id != playerId)
                                                idsToCheck.Add(id);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            List<ulong> friends = FriendsUtil.GetFriendList(playerId);
            if (friends != null)
            {
                foreach (ulong fid in friends)
                {
                    if (fid != playerId)
                        idsToCheck.Add(fid);
                }
            }

            HashSet<ulong> checkedIds = new HashSet<ulong>();
            foreach (ulong id in idsToCheck)
            {
                if (checkedIds.Contains(id))
                    continue;

                checkedIds.Add(id);

                if (IsPlayerOnCooldown(id, out rem))
                {
                    remainingSeconds = rem;
                    return true;
                }
            }

            return false;
        }

        private void ApplyCooldown(ulong playerId)
        {
            double until = Now() + _config.MinutesBeforePlayerCanKillAnotherBradley * 60.0;
            _cooldownUntil[playerId] = until;
        }

        private string FormatDuration(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return string.Format("{0}h {1}m", (int)ts.TotalHours, ts.Minutes);
            return string.Format("{0}m {1}s", ts.Minutes, ts.Seconds);
        }

        #endregion Cooldown Logic

        #region Helper Classes

        public static class PlayerUtil
        {
            public static bool IsNPC(BasePlayer player)
            {
                return player.IsNpc || !player.userID.IsSteamId();
            }

            public static RelationshipManager.PlayerTeam GetTeam(ulong playerId)
            {
                if (RelationshipManager.ServerInstance == null)
                    return null;

                return RelationshipManager.ServerInstance.FindPlayersTeam(playerId);
            }

            public static bool AreTeammates(ulong firstPlayerId, ulong secondPlayerId)
            {
                var team = GetTeam(firstPlayerId);
                if (team != null && team.members.Contains(secondPlayerId))
                    return true;

                return false;
            }
        }

        #endregion Helper Classes

        #region Permissions

        private static class PermissionUtil
        {
            public const string BYPASS = "bradleykillcooldown.bypass";
            private static readonly List<string> _permissions = new List<string>
            {
                BYPASS,
            };

            public static void RegisterPermissions()
            {
                foreach (string perm in _permissions)
                    _plugin.permission.RegisterPermission(perm, _plugin);
            }

            public static bool HasPermission(BasePlayer player, string permission)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permission);
            }
        }

        #endregion Permissions

        #region 3rd Party Integration

        public static class ClansUtil
        {
            private static bool Loaded
            {
                get
                {
                    return _plugin != null &&
                           _plugin.Clans != null &&
                           _plugin.Clans.IsLoaded;
                }
            }

            public static bool ClanExists(string tag)
            {
                if (!Loaded)
                    return false;

                JObject clan = _plugin.Clans.Call<JObject>("GetClan", tag);
                if (clan == null)
                    return false;

                return true;
            }

            public static JObject GetClan(string tag)
            {
                if (!Loaded)
                    return null;

                return _plugin.Clans.Call<JObject>("GetClan", tag);
            }

            public static string GetClanTag(ulong playerId)
            {
                if (!Loaded)
                    return null;

                return _plugin.Clans.Call<string>("GetClanOf", playerId);
            }

            public static bool SameClan(ulong playerId, ulong otherId)
            {
                if (!Loaded)
                    return false;

                return _plugin.Clans.Call<bool>("IsClanMember", playerId, otherId);
            }

            public static bool AreClanAllies(ulong playerId, ulong otherId)
            {
                if (!Loaded)
                    return false;

                return _plugin.Clans.Call<bool>("IsAllyPlayer", playerId, otherId);
            }

            public static bool ShareClanOrAlliance(ulong playerId, ulong otherId)
            {
                if (!Loaded)
                    return false;

                return _plugin.Clans.Call<bool>("IsMemberOrAlly", playerId, otherId);
            }
        }

        public static class FriendsUtil
        {
            private static bool Loaded
            {
                get
                {
                    return _plugin != null &&
                           _plugin.Friends != null &&
                           _plugin.Friends.IsLoaded;
                }
            }

            public static bool AreFriends(ulong playerId, ulong otherId)
            {
                if (!Loaded)
                    return false;

                return _plugin.Friends.Call<bool>("AreFriends", playerId, otherId);
            }

            public static List<ulong> GetFriendList(ulong playerId)
            {
                if (!Loaded)
                    return new List<ulong>();

                ulong[] friendList = _plugin.Friends.Call<ulong[]>("GetFriendList", playerId);

                if (friendList != null)
                    return new List<ulong>(friendList);

                return new List<ulong>();
            }
        }

        #endregion 3rd Party Integration

        #region Localization

        private class Lang
        {
            public const string Notice_OnCooldown = "Notice.OnCooldown";
            public const string Notice_CooldownStarted = "Notice.CooldownStarted";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.Notice_OnCooldown] = "You must wait {0} before engaging another Bradley.",
                [Lang.Notice_CooldownStarted] = "You and your allies are restricted from attacking another Bradley for the next {0}."
            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string userId;
            if (player != null)
                userId = player.UserIDString;
            else
                userId = null;

            string message = _plugin.lang.GetMessage(messageKey, _plugin, userId);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);

            if (!string.IsNullOrWhiteSpace(message))
                _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}