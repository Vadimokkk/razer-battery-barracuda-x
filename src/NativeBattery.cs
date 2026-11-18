// Battery read protocol adapted for the Windows executable. See THIRD_PARTY_NOTICES.md for protocol references.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RazerBarracudaBattery
{
    public sealed class BatteryResult
    {
        public string State;
        public string Message;
        public int Percent;
        public bool Exact;
        public bool Charging;
        public bool ChargingKnown;
        public int Millivolts;
        public int ProductId;
    }

    public static class Monitor
    {
        const ushort VendorId = 0x1532;
        const ushort YSTechPid = 0x0550;
        const ushort MacronixPid = 0x0552;
        const ushort VendorUsagePage = 0xFF00;
        const int InputLength = 64;
        const int OutputLength = 64;
        const int ReadTimeoutMs = 900;
        const int WriteTimeoutMs = 900;
        const int ChargeVoltageMv = 4180;
        const int ChargeJitterMv = 12;
        static readonly int[] Curve = { 3300, 3680, 3750, 3790, 3830, 3870, 3910, 3960, 4050, 4122, 4160 };
        static int sequence;
        static readonly object Gate = new object();
        static readonly object EventGate = new object();
        static readonly Dictionary<string, HeadsetEventMonitor> EventMonitors = new Dictionary<string, HeadsetEventMonitor>(StringComparer.OrdinalIgnoreCase);
        static bool busy;
        static int version;
        static BatteryResult lastResult = new BatteryResult { State = "starting", Message = "Инициализация…", Percent = -1 };

        public static bool IsBusy { get { lock (Gate) return busy; } }
        public static int Version { get { lock (Gate) return version; } }
        public static BatteryResult LastResult { get { lock (Gate) return lastResult; } }

        public static void BeginQuery()
        {
            lock (Gate)
            {
                if (busy) return;
                busy = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryResult result;
                try { result = Query(); }
                catch (Exception ex) { result = new BatteryResult { State = "error", Message = ex.Message, Percent = -1 }; }
                lock (Gate)
                {
                    lastResult = result;
                    busy = false;
                    version++;
                }
            });
        }

        static BatteryResult Query()
        {
            bool foundAny = false;
            bool accessDenied = false;
            List<HidInfo> candidates = Enumerate(out foundAny, out accessDenied);
            if (!foundAny)
            {
                StopEventMonitors();
                BatteryResult bluetooth = ReadBluetoothBattery();
                if (bluetooth != null) return bluetooth;
                return new BatteryResult { State = "offline", Message = "Подключи Barracuda X по Bluetooth или через USB-донгл 2,4 ГГц. Аналоговый кабель 3,5 мм не передаёт данные о батарее.", Percent = -1 };
            }
            if (candidates.Count == 0)
            {
                StopEventMonitors();
                BatteryResult bluetooth = ReadBluetoothBattery();
                if (bluetooth != null) return bluetooth;
                if (accessDenied)
                    return new BatteryResult { State = "access-denied", Message = "Windows запретил доступ к донглу. Закрой Synapse и попробуй запустить приложение от имени администратора.", Percent = -1 };
                return new BatteryResult { State = "unsupported", Message = "Донгл найден, но его интерфейс не распознан. Поддерживаются Barracuda X 2022 (USB ID 1532:0550 и 1532:0552).", Percent = -1 };
            }

            EnsureEventMonitors(candidates);
            string lastError = null;
            foreach (HidInfo info in candidates)
            {
                try
                {
                    BatteryResult result = ReadFrom(info);
                    if (result != null) return MergeHeadsetEvents(info, result);
                }
                catch (UnauthorizedAccessException ex) { accessDenied = true; lastError = ex.Message; }
                catch (Exception ex) { lastError = ex.Message; }
            }
            BatteryResult bluetoothFallback = ReadBluetoothBattery();
            if (bluetoothFallback != null) return bluetoothFallback;
            if (accessDenied)
                return new BatteryResult { State = "access-denied", Message = "Нет доступа к USB-донглу. Закрой Synapse; если не поможет, запусти приложение от имени администратора.", Percent = -1 };
            return new BatteryResult { State = "headset-off", Message = lastError == null ? "Донгл найден. Включи наушники и выбери подключение 2,4 ГГц." : "Донгл найден, но наушники не ответили. Включи их и выбери подключение 2,4 ГГц.", Percent = -1 };
        }

        static void EnsureEventMonitors(List<HidInfo> candidates)
        {
            HashSet<string> active = new HashSet<string>(candidates.Select(delegate(HidInfo info) { return info.Path; }), StringComparer.OrdinalIgnoreCase);
            lock (EventGate)
            {
                List<string> remove = EventMonitors.Keys.Where(delegate(string path) { return !active.Contains(path); }).ToList();
                foreach (string path in remove)
                {
                    EventMonitors[path].Dispose();
                    EventMonitors.Remove(path);
                }

                foreach (HidInfo info in candidates)
                {
                    HeadsetEventMonitor current;
                    if (EventMonitors.TryGetValue(info.Path, out current) && current.Running) continue;
                    if (current != null) current.Dispose();
                    HeadsetEventMonitor monitor = null;
                    monitor = new HeadsetEventMonitor(info, delegate { PublishHeadsetEvents(info, monitor); });
                    EventMonitors[info.Path] = monitor;
                    monitor.Start();
                }
            }
        }

        static void StopEventMonitors()
        {
            lock (EventGate)
            {
                foreach (HeadsetEventMonitor monitor in EventMonitors.Values) monitor.Dispose();
                EventMonitors.Clear();
            }
        }

        static BatteryResult MergeHeadsetEvents(HidInfo info, BatteryResult result)
        {
            HeadsetEventMonitor monitor;
            lock (EventGate) if (!EventMonitors.TryGetValue(info.Path, out monitor)) return result;
            HeadsetEventMonitor.Snapshot events = monitor.Current();
            if (!events.Percent.HasValue && !events.Charger.HasValue) return result;
            int percent = events.Percent.HasValue ? events.Percent.Value : result.Percent;
            bool charging = events.Charger.HasValue ? events.Charger.Value : result.Charging;
            bool chargingKnown = events.Charger.HasValue || result.ChargingKnown;
            bool exact = events.Percent.HasValue || result.Exact;
            bool hasLevel = percent >= 0;
            string message = events.Charger.HasValue
                ? info.ProductId == YSTechPid
                    ? (charging ? "Гарнитура сообщает о зарядке" : "Гарнитура сообщает, что не заряжается")
                    : (charging ? "Зарядный кабель подключён" : "Зарядный кабель отключён")
                : result.Message;
            return new BatteryResult
            {
                State = charging ? "charging" : hasLevel || events.Charger.HasValue ? "online" : result.State == "charging" ? "online" : result.State,
                Message = message,
                Percent = percent,
                Exact = exact,
                Charging = charging,
                ChargingKnown = chargingKnown,
                Millivolts = result.Millivolts,
                ProductId = info.ProductId
            };
        }

        static void PublishHeadsetEvents(HidInfo info, HeadsetEventMonitor monitor)
        {
            HeadsetEventMonitor.Snapshot events = monitor.Current();
            if (!events.Percent.HasValue && !events.Charger.HasValue) return;
            lock (Gate)
            {
                bool sameDevice = lastResult != null && lastResult.ProductId == info.ProductId;
                int percent = events.Percent.HasValue ? events.Percent.Value : sameDevice ? lastResult.Percent : -1;
                bool charging = events.Charger.HasValue ? events.Charger.Value : sameDevice && lastResult.Charging;
                bool exact = events.Percent.HasValue || sameDevice && lastResult.Exact;
                bool chargingKnown = events.Charger.HasValue || sameDevice && lastResult.ChargingKnown;
                string message = events.Charger.HasValue
                    ? info.ProductId == YSTechPid
                        ? (charging ? "Гарнитура сообщает о зарядке" : "Гарнитура сообщает, что не заряжается")
                        : (charging ? "Зарядный кабель подключён" : "Зарядный кабель отключён")
                    : "Уровень батареи сообщает гарнитура";
                lastResult = new BatteryResult
                {
                    State = charging ? "charging" : "online",
                    Message = message,
                    Percent = percent,
                    Exact = exact,
                    Charging = charging,
                    ChargingKnown = chargingKnown,
                    ProductId = info.ProductId
                };
                version++;
            }
        }

        static BatteryResult ReadBluetoothBattery()
        {
            List<IntPtr> radios = EnumerateBluetoothRadios();
            if (radios.Count == 0) return null;
            Guid classGuid = Guid.Empty;
            IntPtr set = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, 0x00000002 | 0x00000004);
            if (set == new IntPtr(-1)) { CloseBluetoothRadios(radios); return null; }
            try
            {
                DEVPROPKEY batteryKey = new DEVPROPKEY();
                batteryKey.fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
                batteryKey.pid = 2;
                for (uint index = 0; ; index++)
                {
                    SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                    StringBuilder instance = new StringBuilder(512);
                    int required;
                    if (!SetupDiGetDeviceInstanceId(set, ref data, instance, instance.Capacity, out required)) continue;
                    string id = instance.ToString();
                    if (!id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) &&
                        !id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase)) continue;

                    byte[] buffer = new byte[4];
                    uint propertyType, propertySize;
                    propertySize = (uint)buffer.Length;
                    if (CM_Get_DevNode_PropertyW(unchecked((uint)data.DevInst), ref batteryKey, out propertyType,
                        buffer, ref propertySize, 0) != 0 || propertyType != 3 || propertySize < 1 || buffer[0] > 100) continue;

                    ulong address = ParseBluetoothAddress(id);
                    if (address == 0) continue;
                    BluetoothDeviceInfo device = new BluetoothDeviceInfo();
                    bool connected = false;
                    foreach (IntPtr radio in radios)
                    {
                        device = new BluetoothDeviceInfo();
                        device.dwSize = (uint)Marshal.SizeOf(typeof(BluetoothDeviceInfo));
                        device.Address = address;
                        if (BluetoothGetDeviceInfo(radio, ref device) == 0 && device.fConnected) { connected = true; break; }
                    }
                    if (!connected) continue;
                    string name = device.szName ?? "";
                    if (name.IndexOf("Barracuda", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    return new BatteryResult
                    {
                        State = "bluetooth",
                        Message = "Bluetooth · уровень сообщает Windows" + (buffer[0] <= 20 ? " · пора зарядить" : ""),
                        Percent = buffer[0],
                        Exact = true,
                        Charging = false,
                        ChargingKnown = false,
                        ProductId = 0
                    };
                }
            }
            catch { /* Bluetooth is optional; continue with the USB dongle provider. */ }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
                CloseBluetoothRadios(radios);
            }
            return null;
        }

        static List<IntPtr> EnumerateBluetoothRadios()
        {
            List<IntPtr> radios = new List<IntPtr>();
            BLUETOOTH_FIND_RADIO_PARAMS parameters = new BLUETOOTH_FIND_RADIO_PARAMS();
            parameters.dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
            IntPtr firstRadio;
            IntPtr find = BluetoothFindFirstRadio(ref parameters, out firstRadio);
            if (find == IntPtr.Zero) return radios;
            if (firstRadio != IntPtr.Zero) radios.Add(firstRadio);
            try
            {
                IntPtr radio;
                while (BluetoothFindNextRadio(find, out radio))
                    if (radio != IntPtr.Zero) radios.Add(radio);
            }
            finally { BluetoothFindRadioClose(find); }
            return radios;
        }

        static void CloseBluetoothRadios(List<IntPtr> radios)
        {
            foreach (IntPtr radio in radios) if (radio != IntPtr.Zero) CloseHandle(radio);
        }

        static ulong ParseBluetoothAddress(string id)
        {
            MatchCollection matches = Regex.Matches(id, "(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])", RegexOptions.IgnoreCase);
            if (matches.Count == 0) return 0;
            ulong address;
            return UInt64.TryParse(matches[matches.Count - 1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) ? address : 0;
        }

        static BatteryResult ReadFrom(HidInfo info)
        {
            using (HidDevice dev = new HidDevice(info.Path))
            {
                Drain(dev);
                byte id = info.ProductId == YSTechPid ? (byte)0x02 : (byte)0x01;
                if (id != 0x02 && !EnterMacronixMode(dev, id)) return null;

                byte[] remote = Race(dev, info.ProductId, 0x0E, 0x02, 0xE1, 0x01);
                if (remote == null || remote.Length < 13 || remote[12] != 0x00) return null;
                int percent = -1;
                int millivolts = -1;
                bool exact = false;
                bool charging = false;
                bool chargingKnown = false;
                try
                {
                    if (info.ProductId == MacronixPid)
                    {
                        byte[] batteryReply = ReadCustomerValue(dev, 0x21);
                        if (batteryReply != null && batteryReply[13] <= 100)
                        {
                            percent = batteryReply[13];
                            exact = true;
                        }

                        byte[] cableReply = ReadCustomerValue(dev, 0x2A);
                        if (cableReply != null && cableReply[13] <= 1)
                        {
                            charging = cableReply[13] == 1;
                            chargingKnown = true;
                        }
                    }

                    if (percent < 0)
                    {
                        List<int> samples = new List<int>();
                        for (int i = 0; i < 7; i++)
                        {
                            if (i > 0) Thread.Sleep(350);
                            byte[] reply = Race(dev, info.ProductId, 0x06, 0x01, 0x00, 0x31);
                            if (reply == null || reply.Length < 15 || reply[12] != 0x00) continue;
                            int mv = reply[13] | (reply[14] << 8);
                            if (mv >= 2500 && mv <= 4500) samples.Add(mv);
                        }
                        if (samples.Count == 0) return null;
                        samples.Sort();
                        int trim = samples.Count >= 5 ? 1 : 0;
                        int low = samples[trim];
                        int high = samples[samples.Count - 1 - trim];
                        millivolts = low;
                        charging = high - low >= ChargeJitterMv || low >= ChargeVoltageMv;
                        if (!charging) percent = ToPercent(low);
                    }
                }
                finally
                {
                    try { Race(dev, info.ProductId, 0x0E, 0x02, 0xE1, 0x00); } catch { }
                }
                string message = chargingKnown
                    ? (charging ? "Кабель зарядки подключён · уровень сообщает гарнитура" : "Наушники подключены через донгл 2,4 ГГц")
                    : charging ? "Возможна зарядка · оценка по напряжению" : exact ? "Уровень батареи сообщает гарнитура" : "Наушники подключены через донгл 2,4 ГГц";
                return new BatteryResult
                {
                    State = charging ? "charging" : "online",
                    Message = message,
                    Percent = percent,
                    Exact = exact,
                    Charging = charging,
                    ChargingKnown = chargingKnown,
                    Millivolts = millivolts,
                    ProductId = info.ProductId
                };
            }
        }

        static byte[] ReadCustomerValue(HidDevice dev, byte parameter)
        {
            byte seq = (byte)(Interlocked.Increment(ref sequence) % 127 + 1);
            byte[] request = { 0x50, 0x41, 0x08, seq, 0x03, parameter, 0x00, 0x00 };
            Func<byte[], bool> matches = delegate(byte[] frame)
            {
                return frame.Length == 14 && frame[0] == 0x50 && frame[1] == 0x49 &&
                    frame[2] == 0x08 && frame[8] == 0x04 && frame[9] == 0x00 &&
                    frame[10] == parameter && frame[11] == 0x01 && frame[12] == 0x01;
            };
            return SendMacronix(dev, request, matches);
        }

        static void Drain(HidDevice dev)
        {
            DateTime stop = DateTime.UtcNow.AddMilliseconds(80);
            while (DateTime.UtcNow < stop)
            {
                if (dev.Read(20) == null) break;
            }
        }

        static byte[] Race(HidDevice dev, ushort pid, byte group, params byte[] body)
        {
            byte seq = (byte)(Interlocked.Increment(ref sequence) % 127 + 1);
            byte[] payload = new byte[4 + body.Length];
            payload[0] = 0x50; payload[1] = 0x41; payload[2] = group; payload[3] = seq;
            Array.Copy(body, 0, payload, 4, body.Length);
            Func<byte[], bool> matches = delegate(byte[] f)
            {
                return f.Length >= 13 && f[0] == 0x50 && f[1] == 0x49 && f[10] == group && f[11] == (byte)(0x80 | seq);
            };
            return pid == YSTechPid ? SendYsTech(dev, payload, matches) : SendMacronix(dev, payload, matches);
        }

        static bool EnterMacronixMode(HidDevice dev, byte id)
        {
            byte[] report = new byte[OutputLength];
            report[0] = id; report[1] = 0x40;
            if (!dev.Write(report, WriteTimeoutMs)) return false;
            byte[] mode = ReadUntil(dev, delegate(byte[] r) { return r.Length >= 4 && r[0] == id && r[1] == 0x40 ? r : null; });
            return mode != null && mode[2] == 0x01 && mode[3] == 0x01;
        }

        static byte[] SendYsTech(HidDevice dev, byte[] race, Func<byte[], bool> matches)
        {
            byte[] frame = new byte[OutputLength];
            frame[0] = 0x02; frame[1] = 0x80; frame[2] = (byte)race.Length;
            Array.Copy(race, 0, frame, 5, race.Length);
            ushort crc = Crc16Xmodem(frame);
            frame[3] = (byte)crc; frame[4] = (byte)(crc >> 8);
            bool accepted = false;
            for (int attempt = 0; attempt < 5 && !accepted; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                if (!dev.Write(frame, WriteTimeoutMs)) return null;
                byte[] ack = dev.GetInputReport(0x02);
                if (ack == null || ack.Length < 2) return null;
                if (ack[1] == (byte)'O') accepted = true;
                else if (ack[1] != (byte)'F') return null;
            }
            if (!accepted) return null;
            return ReadUntil(dev, delegate(byte[] r)
            {
                if (r.Length < 2 || r[0] != 0x02 || r[1] > r.Length - 2) return null;
                return FindFrame(r, 2, r[1], matches);
            });
        }

        static byte[] SendMacronix(HidDevice dev, byte[] race, Func<byte[], bool> matches)
        {
            byte[] frame = new byte[OutputLength];
            frame[0] = 0x01; frame[1] = 0x80; frame[2] = (byte)race.Length;
            Array.Copy(race, 0, frame, 3, race.Length);
            if (!dev.Write(frame, WriteTimeoutMs)) return null;
            return ReadUntil(dev, delegate(byte[] r)
            {
                if (r.Length < 3 || r[0] != 0x01 || r[1] != 0x80 || r[2] > r.Length - 3) return null;
                return FindFrame(r, 3, r[2], matches);
            });
        }

        static byte[] ReadUntil(HidDevice dev, Func<byte[], byte[]> extract)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                byte[] report = dev.Read(remaining);
                if (report == null) return null;
                byte[] answer = extract(report);
                if (answer != null) return answer;
            }
            return null;
        }

        static byte[] FindFrame(byte[] report, int offset, int length, Func<byte[], bool> matches)
        {
            int end = Math.Min(offset + length, report.Length);
            for (int pos = offset; pos + 10 <= end;)
            {
                if (report[pos] != 0x50 || report[pos + 1] != 0x49) break;
                int payloadLength = report[pos + 8] | (report[pos + 9] << 8);
                int frameLength = 10 + payloadLength;
                if (pos + frameLength > end) break;
                byte[] f = new byte[frameLength];
                Array.Copy(report, pos, f, 0, frameLength);
                if (matches(f)) return f;
                pos += frameLength;
            }
            return null;
        }

        static ushort Crc16Xmodem(byte[] data)
        {
            ushort crc = 0;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        static int ToPercent(int mv)
        {
            if (mv <= Curve[0]) return 0;
            for (int i = 1; i < Curve.Length; i++)
                if (mv < Curve[i]) return (i - 1) * 10 + (mv - Curve[i - 1]) * 10 / (Curve[i] - Curve[i - 1]);
            return 100;
        }

        sealed class HeadsetEventMonitor : IDisposable
        {
            readonly HidInfo info;
            readonly Action changed;
            readonly object stateGate = new object();
            Thread thread;
            volatile bool stopped;
            public volatile bool Running;
            int? percent;
            bool? charger;
            bool? linked;

            public struct Snapshot
            {
                public int? Percent;
                public bool? Charger;
            }

            public HeadsetEventMonitor(HidInfo device, Action onChanged)
            {
                info = device;
                changed = onChanged;
            }

            public void Start()
            {
                if (Running || stopped) return;
                Running = true;
                thread = new Thread(Listen);
                thread.IsBackground = true;
                thread.Name = "Barracuda battery events";
                thread.Start();
            }

            void Listen()
            {
                try
                {
                    using (HidDevice dev = new HidDevice(info.Path, true))
                    {
                        while (!stopped)
                        {
                            byte[] report = dev.Read(1000);
                            if (!stopped && report != null) Process(report);
                        }
                    }
                }
                catch { /* A later battery poll will restart this listener if the dongle reconnects. */ }
                finally { Running = false; }
            }

            void Process(byte[] report)
            {
                int offset;
                int payloadLength;
                if (info.ProductId == YSTechPid)
                {
                    if (report.Length < 2 || report[0] != 0x02) return;
                    offset = 2;
                    payloadLength = report[1];
                }
                else
                {
                    if (report.Length < 3 || report[0] != 0x01 || report[1] != 0x80) return;
                    offset = 3;
                    payloadLength = report[2];
                }

                int end = Math.Min(report.Length, offset + payloadLength);
                for (int pos = offset; pos + 10 <= end;)
                {
                    if (report[pos] != 0x50 || report[pos + 1] != 0x49) break;
                    int framePayload = report[pos + 8] | (report[pos + 9] << 8);
                    int frameLength = 10 + framePayload;
                    if (pos + frameLength > end) break;
                    if (framePayload >= 4 && report[pos + 2] == 0x08 &&
                        report[pos + 8] == 0x04 && report[pos + 9] == 0x00 &&
                        report[pos + 11] == 0x02 && report[pos + 12] == 0x01)
                        ApplyNotification(report[pos + 10], report[pos + 13]);
                    pos += frameLength;
                }
            }

            void ApplyNotification(byte parameter, byte value)
            {
                bool changedValue = false;
                lock (stateGate)
                {
                    if (parameter == 0x20 && value <= 1)
                    {
                        bool isLinked = value == 1;
                        if (!linked.HasValue || linked.Value != isLinked) { linked = isLinked; changedValue = true; }
                        if (!isLinked && charger.HasValue) { charger = null; changedValue = true; }
                    }
                    else if (parameter == 0x21 && value <= 100)
                    {
                        if (!percent.HasValue || percent.Value != value) { percent = value; changedValue = true; }
                    }
                    else if (parameter == 0x2A && value <= 1)
                    {
                        bool isCharging = value == 1;
                        if (!charger.HasValue || charger.Value != isCharging) { charger = isCharging; changedValue = true; }
                    }
                }
                if (changedValue && changed != null) changed();
            }

            public Snapshot Current()
            {
                lock (stateGate) return new Snapshot { Percent = percent, Charger = charger };
            }

            public void Dispose()
            {
                stopped = true;
                Thread worker = thread;
                if (worker != null && worker != Thread.CurrentThread) worker.Join(1200);
            }
        }

        sealed class HidInfo
        {
            public string Path;
            public ushort ProductId;
            public ushort InputLength;
            public ushort OutputLength;
        }

        static List<HidInfo> Enumerate(out bool foundAny, out bool accessDenied)
        {
            foundAny = false; accessDenied = false;
            List<HidInfo> result = new List<HidInfo>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x00000002 | 0x00000010);
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (uint index = 0; ; index++)
                {
                    SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data)) break;
                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (String.IsNullOrEmpty(path)) continue;
                        string upper = path.ToUpperInvariant();
                        ushort pid = upper.Contains("PID_0550") ? YSTechPid : upper.Contains("PID_0552") ? MacronixPid : (ushort)0;
                        if (!upper.Contains("VID_1532") || pid == 0) continue;
                        foundAny = true;
                        using (SafeFileHandle h = CreateFile(path, 0xC0000000, 0x00000003, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
                        {
                            if (h.IsInvalid)
                            {
                                if (Marshal.GetLastWin32Error() == 5) accessDenied = true;
                                continue;
                            }
                            IntPtr ppd;
                            if (!HidD_GetPreparsedData(h, out ppd)) continue;
                            try
                            {
                                HIDP_CAPS caps = new HIDP_CAPS();
                                caps.Reserved = new ushort[17];
                                int status = HidP_GetCaps(ppd, ref caps);
                                if (status != 0x00110000 || caps.UsagePage != VendorUsagePage || caps.InputReportByteLength != InputLength || caps.OutputReportByteLength != OutputLength) continue;
                                result.Add(new HidInfo { Path = path, ProductId = pid, InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength });
                            }
                            finally { HidD_FreePreparsedData(ppd); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result.OrderByDescending(i => i.InputLength == InputLength && i.OutputLength == OutputLength).ToList();
        }

        sealed class HidDevice : IDisposable
        {
            readonly SafeFileHandle handle;
            readonly int inputLength;
            readonly int outputLength;
            public HidDevice(string path) : this(path, false) { }
            public HidDevice(string path, bool readOnly)
            {
                uint access = readOnly ? 0x80000000 : 0xC0000000;
                handle = CreateFile(path, access, 0x00000003, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error == 5) throw new UnauthorizedAccessException("Access denied");
                    throw new InvalidOperationException("Не удалось открыть USB-донгл (ошибка " + error + ").");
                }
                inputLength = InputLength; outputLength = OutputLength;
            }
            public bool Write(byte[] report, int timeout)
            {
                byte[] data = new byte[outputLength];
                Array.Copy(report, data, Math.Min(report.Length, data.Length));
                return Transfer(data, true, timeout) != null;
            }
            public byte[] GetInputReport(byte id)
            {
                byte[] data = new byte[inputLength]; data[0] = id;
                GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                try { return HidD_GetInputReport(handle, pin.AddrOfPinnedObject(), data.Length) ? data : null; }
                finally { pin.Free(); }
            }
            public byte[] Read(int timeout)
            {
                return Transfer(new byte[inputLength], false, timeout);
            }
            byte[] Transfer(byte[] data, bool write, int timeout)
            {
                IntPtr ev = CreateEvent(IntPtr.Zero, true, false, null);
                if (ev == IntPtr.Zero) return null;
                OVERLAPPED ov = new OVERLAPPED(); ov.hEvent = ev;
                GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    uint transferred = 0;
                    bool ok = write
                        ? WriteFile(handle, pin.AddrOfPinnedObject(), (uint)data.Length, IntPtr.Zero, ref ov)
                        : ReadFile(handle, pin.AddrOfPinnedObject(), (uint)data.Length, IntPtr.Zero, ref ov);
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 997) return null;
                        uint wait = WaitForSingleObject(ev, (uint)timeout);
                        if (wait == 0x00000102)
                        {
                            CancelIoEx(handle, ref ov);
                            WaitForSingleObject(ev, 0xFFFFFFFF);
                            return null;
                        }
                        if (wait != 0 || !GetOverlappedResult(handle, ref ov, out transferred, false)) return null;
                    }
                    else
                    {
                        // A completed overlapped call still signals its event; obtain the final byte count.
                        if (!GetOverlappedResult(handle, ref ov, out transferred, true)) return null;
                    }
                    if (write) return new byte[] { 1 };
                    if (transferred == 0) return null;
                    if (transferred < data.Length) Array.Resize(ref data, (int)transferred);
                    return data;
                }
                finally { pin.Free(); CloseHandle(ev); }
            }
            public void Dispose() { if (handle != null) handle.Dispose(); }
        }

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }
        [StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct BluetoothDeviceInfo
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [StructLayout(LayoutKind.Sequential)] struct OVERLAPPED { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr hEvent; }
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid HidGuid);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetInputReport(SafeFileHandle HidDeviceObject, IntPtr ReportBuffer, int ReportBufferLength);
        [DllImport("hid.dll", SetLastError = true)] static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle hFile, IntPtr buffer, uint bytesToRead, IntPtr bytesRead, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle hFile, IntPtr buffer, uint bytesToWrite, IntPtr bytesWritten, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle hFile, ref OVERLAPPED overlapped, out uint bytesTransferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle hFile, ref OVERLAPPED overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BluetoothDeviceInfo pbtdi);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", SetLastError = true)] static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType, byte[] PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);
    }
}
