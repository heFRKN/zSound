using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace zSound.Audio
{
    /// cihaz takıldı/çıkarıldı/kuruldu → Changed (COM thread'inden gelir)
    public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        readonly MMDeviceEnumerator en = new MMDeviceEnumerator();
        public event Action Changed;

        public DeviceWatcher() => en.RegisterEndpointNotificationCallback(this);

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();
        public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();
        public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { en.UnregisterEndpointNotificationCallback(this); } catch { }
            en.Dispose();
        }
    }
}
