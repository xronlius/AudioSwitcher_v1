using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AudioSwitcher.AudioApi;

namespace FortyOne.AudioSwitcher
{
    public static class FavouriteDeviceManager
    {
        public delegate void FavouriteDeviceIDsChangedEventHandler(List<Guid> ids);

        private static List<Guid> FavouriteDeviceIDs = new List<Guid>();

        static FavouriteDeviceManager()
        {
            FavouriteDeviceIDs = new List<Guid>();
        }

        public static int FavouriteDeviceCount
        {
            get { return FavouriteDeviceIDs.Count; }
        }

        public static int FavouritePlaybackDeviceCount
        {
            get
            {
                return FavouriteDeviceIDs.Count(id =>
                {
                    var device = DeviceResolver.Resolve(id);
                    return device != null && device.IsPlaybackDevice;
                });
            }
        }

        public static ReadOnlyCollection<Guid> FavouriteDevices
        {
            get { return new ReadOnlyCollection<Guid>(FavouriteDeviceIDs); }
        }

        public static event FavouriteDeviceIDsChangedEventHandler FavouriteDevicesChanged;

        public static bool LoadFavouriteDevices(List<Guid> favouriteIDs)
        {
            return LoadFavouriteDevices(favouriteIDs.ToArray());
        }

        public static bool LoadFavouriteDevices(Guid[] favouriteIDs)
        {
            FavouriteDeviceIDs = new List<Guid>();

            foreach (var s in favouriteIDs)
            {
                if (s == Guid.Empty)
                    continue;

                //Keep favourites even if the headset is off right now; re-bind if its ID changed
                var device = DeviceResolver.Resolve(s);
                AddFavouriteDevice(device != null ? device.Id : s);
            }
            return true;
        }

        public static bool IsFavouriteDevice(IDevice ad)
        {
            if (ad == null)
                return false;

            if (FavouriteDeviceIDs.Contains(ad.Id))
                return true;

            return RebindTo(ad);
        }

        /// <summary>
        ///     If a saved favourite ID now resolves (by name) to this device, swap the stored ID.
        /// </summary>
        private static bool RebindTo(IDevice ad)
        {
            for (var i = 0; i < FavouriteDeviceIDs.Count; i++)
            {
                var id = FavouriteDeviceIDs[i];
                if (DeviceResolver.ResolveId(id) != ad.Id)
                    continue;

                FavouriteDeviceIDs[i] = ad.Id;
                FireFavouriteDeviceChanged();
                return true;
            }

            return false;
        }

        public static bool IsFavouriteDevice(Guid id)
        {
            return FavouriteDeviceIDs.Contains(id);
        }

        public static Guid AddFavouriteDevice(Guid id)
        {
            if (FavouriteDeviceIDs.Contains(id))
                return Guid.Empty;

            FavouriteDeviceIDs.Add(id);

            FireFavouriteDeviceChanged();

            return id;
        }

        public static Guid RemoveFavouriteDevice(Guid id)
        {
            FavouriteDeviceIDs.Remove(id);

            FireFavouriteDeviceChanged();

            return id;
        }

        private static void FireFavouriteDeviceChanged()
        {
            if (FavouriteDevicesChanged != null)
                FavouriteDevicesChanged(FavouriteDeviceIDs);
        }

        public static IDevice GetNextFavouritePlaybackDevice(IDevice device)
        {
            var nextDeviceId = GetNextFavouritePlaybackDeviceId(device != null ? device.Id : Guid.Empty);
            return DeviceResolver.Resolve(nextDeviceId);
        }

        public static Guid GetNextFavouritePlaybackDeviceId(Guid deviceId)
        {
            var count = FavouriteDeviceIDs.Count;
            if (count == 0)
                return Guid.Empty;

            var index = 0;

            if (deviceId != Guid.Empty)
            {
                //Start at the next device (match by resolved ID in case the stored one is stale)
                var current = FavouriteDeviceIDs.FindIndex(x => x == deviceId || DeviceResolver.ResolveId(x) == deviceId);
                index = (current + 1) % count;
            }

            //Check each favourite once. The old loop never ended if no favourite was active,
            //which froze the app while a wireless headset was switched off.
            for (var n = 0; n < count; n++)
            {
                var id = FavouriteDeviceIDs[(index + n) % count];
                var ad = DeviceResolver.Resolve(id);

                if (ad == null || ad.State != DeviceState.Active)
                    continue;

                if (ad.DeviceType == DeviceType.Playback)
                    return ad.Id;
            }

            return Guid.Empty;
        }
    }
}