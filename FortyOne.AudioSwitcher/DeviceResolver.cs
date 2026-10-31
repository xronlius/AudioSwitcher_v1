using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AudioSwitcher.AudioApi;

namespace FortyOne.AudioSwitcher
{
    /// <summary>
    ///     Wireless / Bluetooth / USB-dongle headsets often get a brand new Windows endpoint ID when they
    ///     reconnect, re-pair, or are plugged into a different USB port. Everything in AudioSwitcher
    ///     (hotkeys, favourites, startup device) was saved by that ID, so the headset turned into
    ///     "Unknown Device". This class remembers the name of every device it sees and, when a saved ID
    ///     can no longer be found (or only points at a disconnected copy), finds the same device by name.
    /// </summary>
    public static class DeviceResolver
    {
        private const string SETTING_KEY = "KnownDeviceNames";
        private static readonly object Sync = new object();
        private static readonly Regex PortPrefix = new Regex(@"\(\s*\d+\s*-\s*", RegexOptions.Compiled);
        private static Dictionary<Guid, KnownDevice> _known;

        private class KnownDevice
        {
            public string FullName;
            public DeviceType Type;
        }

        /// <summary>
        ///     Find the device for a saved ID. Falls back to matching by name when the ID is gone
        ///     or the ID only refers to a stale, non-active copy while the real headset is active.
        /// </summary>
        public static IDevice Resolve(Guid id)
        {
            if (id == Guid.Empty)
                return null;

            IDevice device = null;
            try
            {
                device = AudioDeviceManager.Controller.GetDevice(id);
            }
            catch
            {
                //treat as not found
            }

            if (device != null && device.State == DeviceState.Active && HasUsableName(device))
            {
                Remember(device);
                return device;
            }

            //Work out which name we are looking for
            string fullName = null;
            DeviceType? type = null;

            if (device != null && HasUsableName(device))
            {
                fullName = device.FullName;
                type = device.DeviceType;
            }
            else
            {
                var known = GetKnown(id);
                if (known != null)
                {
                    fullName = known.FullName;
                    type = known.Type;
                }
            }

            if (fullName == null)
                return device;

            var match = FindByName(fullName, type, id);
            if (match != null)
            {
                Remember(match);
                //keep the old id pointing at the name too, so other saved references still resolve
                RememberAs(id, match);
                return match;
            }

            return device;
        }

        /// <summary>
        ///     Same as <see cref="Resolve" /> but returns the current ID (or the original if unresolved).
        /// </summary>
        public static Guid ResolveId(Guid id)
        {
            var device = Resolve(id);
            return device != null ? device.Id : id;
        }

        public static void Remember(IDevice device)
        {
            if (device == null || !HasUsableName(device))
                return;

            RememberAs(device.Id, device);
        }

        public static void RememberAll(IEnumerable<IDevice> devices)
        {
            if (devices == null)
                return;

            foreach (var d in devices)
                Remember(d);
        }

        private static void RememberAs(Guid id, IDevice device)
        {
            lock (Sync)
            {
                EnsureLoaded();

                KnownDevice existing;
                if (_known.TryGetValue(id, out existing)
                    && existing.FullName == device.FullName
                    && existing.Type == device.DeviceType)
                    return;

                _known[id] = new KnownDevice { FullName = device.FullName, Type = device.DeviceType };
                Save();
            }
        }

        private static KnownDevice GetKnown(Guid id)
        {
            lock (Sync)
            {
                EnsureLoaded();
                KnownDevice k;
                return _known.TryGetValue(id, out k) ? k : null;
            }
        }

        private static IDevice FindByName(string fullName, DeviceType? type, Guid excludeId)
        {
            var wanted = Normalize(fullName);
            List<IDevice> candidates;

            try
            {
                candidates = new List<IDevice>();
                if (type == null || type == DeviceType.Playback || type == DeviceType.All)
                    candidates.AddRange(AudioDeviceManager.Controller.GetPlaybackDevices(DeviceState.All));
                if (type == null || type == DeviceType.Capture || type == DeviceType.All)
                    candidates.AddRange(AudioDeviceManager.Controller.GetCaptureDevices(DeviceState.All));
            }
            catch
            {
                return null;
            }

            var matches = candidates
                .Where(d => d != null && HasUsableName(d) && Normalize(d.FullName) == wanted)
                .ToList();

            //Prefer an active device; otherwise any other copy that isn't the one we already failed on
            return matches.FirstOrDefault(d => d.State == DeviceState.Active)
                   ?? matches.FirstOrDefault(d => d.Id != excludeId);
        }

        /// <summary>
        ///     "Headphones (2- Arctis 7 Game)" and "Headphones (Arctis 7 Game)" are the same headset:
        ///     Windows adds the "2- " prefix when the device shows up on another port.
        /// </summary>
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            return PortPrefix.Replace(name.Trim(), "(").ToLowerInvariant();
        }

        private static bool HasUsableName(IDevice device)
        {
            try
            {
                var name = device.FullName;
                return !string.IsNullOrWhiteSpace(name)
                       && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Stored as [guid|type|base64(name)][guid|type|base64(name)]...
        private static void EnsureLoaded()
        {
            if (_known != null)
                return;

            _known = new Dictionary<Guid, KnownDevice>();

            string raw = null;
            try
            {
                raw = Program.Settings.KnownDeviceNames;
            }
            catch
            {
                //setting doesn't exist yet
            }

            if (string.IsNullOrEmpty(raw))
                return;

            foreach (var entry in raw.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('|');
                if (parts.Length != 3)
                    continue;

                try
                {
                    var id = new Guid(parts[0]);
                    var type = (DeviceType)int.Parse(parts[1]);
                    var name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    _known[id] = new KnownDevice { FullName = name, Type = type };
                }
                catch
                {
                    //skip bad entries
                }
            }
        }

        private static void Save()
        {
            var sb = new StringBuilder();
            foreach (var kv in _known)
            {
                sb.Append('[')
                  .Append(kv.Key)
                  .Append('|')
                  .Append((int)kv.Value.Type)
                  .Append('|')
                  .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(kv.Value.FullName)))
                  .Append(']');
            }

            try
            {
                Program.Settings.KnownDeviceNames = sb.ToString();
            }
            catch
            {
                //memory-only if the settings file can't be written
            }
        }
    }
}
