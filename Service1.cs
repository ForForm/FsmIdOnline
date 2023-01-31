using FSM_Authorization;
using FsmIdOnline.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using static FsmIdOnlineService.UserTranslator;

namespace FsmIdOnlineService
{



    public partial class FsmIdOnlineService : ServiceBase
    {

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(System.IntPtr handle, ref ServiceStatus serviceStatus);

        public enum ServiceState
        {
            SERVICE_STOPPED = 0x00000001,
            SERVICE_START_PENDING = 0x00000002,
            SERVICE_STOP_PENDING = 0x00000003,
            SERVICE_RUNNING = 0x00000004,
            SERVICE_CONTINUE_PENDING = 0x00000005,
            SERVICE_PAUSE_PENDING = 0x00000006,
            SERVICE_PAUSED = 0x00000007,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus
        {
            public int dwServiceType;
            public ServiceState dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        };



        public static bool isRunning = false;
        public static bool Executing = false;        
        List<DeviceInfo> list;
        string connString = ConfigurationManager.ConnectionStrings["DefaultConnection"].ToString();


        public FsmIdOnlineService()
        {
            InitializeComponent();
 
        }

        public static void LogMessage(string device, string msg)
        {
            //System.Environment.CurrentDirectory
            System.IO.StreamWriter sw;
            try
            {
                string sFilePath;
                if (device==string.Empty)
                    sFilePath = AppDomain.CurrentDomain.BaseDirectory + @"\Log\" + DateTime.Now.ToString("MMddyyyy") + ".txt";
                else
                    sFilePath = AppDomain.CurrentDomain.BaseDirectory + @"\Log\"+ device+  DateTime.Now.ToString("MMddyyyy") + ".txt";
                sw = System.IO.File.AppendText(sFilePath);
                string logLine = System.String.Format(
                    "{0:G}: {1}.", System.DateTime.Now, msg);
                sw.WriteLine(logLine);
                sw.Close();
            }
            catch
            {}

        }

        protected override void OnStart(string[] args)
        {
            LogMessage(string.Empty,"In OnStart.");
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_START_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
            list = new List<DeviceInfo>();
            isRunning = false;
            System.Timers.Timer timer = new System.Timers.Timer();
            timer.Interval = 5000;
            timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
            timer.Start();



        }

        public void OnTimer(object sender, ElapsedEventArgs args)
        {

            try
            {
                if (!isRunning)
                {
                    isRunning = true;
                    bool blSqlConnection = false;
                    string deviceID = @System.Configuration.ConfigurationManager.AppSettings["DeviceID"];
                    SqlParameter[] param = { new SqlParameter("@DeviceID", deviceID) };
                    try
                    {
                        if (!blSqlConnection)
                        {
                            LogMessage(string.Empty,deviceID);
                            list = SqlHelper.ExecuteProcedureReturnData<List<DeviceInfo>>(connString, "CKS_HAREKET_DEVICE", r => r.GetDevices(), param);
                            foreach (var item in list)
                            {
                                LogMessage(string.Empty,item.cIP);
                            }
                            blSqlConnection = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        isRunning = false;
                        blSqlConnection = false;
                        LogMessage(string.Empty," SQL server bağlantı hatası sonrası hata oluştu. Hata: " + ex.Message + "-" + ex.InnerException);
                        LogMessage(string.Empty, " SQL server bağlantı hatası sonrası hata oluştu. Hata: " + ex.Message + "-" + ex.InnerException);
                    }
                }

                if (!Executing)
                {
                    Thread[] threadOnline = new Thread[list.Count];
                    int i = 0;
                    foreach (var item in list)
                    {
                        try
                        {
                            threadOnline[i] = new Thread(() => ListenOnline(item));
                            threadOnline[i].Start();
                            i++;

                        }
                        catch
                        { }

                    }

                    LogMessage(string.Empty, "Sistem izleniyor...");
                }

            }
            catch (Exception ex)
            {
                isRunning = false;
                LogMessage(string.Empty, "Servis hata..." + ex.Message + "-" + ex.InnerException);
            }



        }

        void insertDB(string ip, string cardId, DateTime zaman, int yon, string dscr, string ex)
        {
            SqlParameter[] param = {
                new SqlParameter("@SRKODU", 1),
                new SqlParameter("@KARTNO", cardId),
                new SqlParameter("@ZAMAN_TRH", zaman),
                new SqlParameter("@YON", yon),
                new SqlParameter("@ACIKLAMA", dscr),
                new SqlParameter("@CIHAZ", ip),
                new SqlParameter("@HATA", ex),

            };
            SqlHelper.ExecuteProcedureReturnString(connString, "CKS_HAREKET_INSERT", param);
        }


        bool checkDB(string cardID, DeviceInfo device)
        {
            SqlParameter[] param = {
                new SqlParameter("@CardID", cardID),
                new SqlParameter("@Device", device.cIP),
            };
            string result = SqlHelper.ExecuteProcedureReturnString(connString, "CKS_HAREKET_CARD", param);

            if (result == "NOTEXISTS")
            {
                LogMessage(device.cId,device.cIP + "---" + device.cId + "---" + cardID + " tanımsız kart");
                return false;
            }
            else
                return true;
        }


        void ListenOnline(DeviceInfo deviceInfo)
        {
            Executing = true;
            FSM_Authorization.ConnectionManager.ReturnValues Result;
            FSM_Authorization.ConnectionManager.Converter Cnv = ConnectionManager.Converter.NewConv;
            FSM_Authorization.ConnectionManager fsm = new ConnectionManager();
            TcpClient client = null;
            int waitTime = Convert.ToInt32(@System.Configuration.ConfigurationManager.AppSettings["WaitTime"]);
            int timeout = Convert.ToInt32(@System.Configuration.ConfigurationManager.AppSettings["Timeout"]);
            UInt64 ID; int Address; int OfflineLogCount; byte[] temp;
            ConnectionManager.DoorStatus Door;
            ConnectionManager.CardStatus Kart;
            ConnectionManager.EmergencySts Emergency;
            string cardId = string.Empty;
            List<CardInfo> list = new List<CardInfo>();
            DateTime controlZaman = DateTime.Now;

            try
            {
                client = new TcpClient();
                fsm.PingAndPortTest(deviceInfo.cIP, deviceInfo.Port, client);
                LogMessage(deviceInfo.cId, deviceInfo.cIP + "---" + deviceInfo.cId + " Cihaz dinleniyor..");
            }
            catch (Exception ex)
            {
                LogMessage(deviceInfo.cId, deviceInfo.cIP + "---" + deviceInfo.cId + " nolu cihaz bağlanamadı.Hata:" + ex.Message);
                Executing = false;
            }

            while (true)
            {
                try

                {

                    try
                    {
                        if (client.Client == null)
                        {
                            client.Close(); client = null;
                            Console.WriteLine("Hata:" + deviceInfo.cIP + "---" + deviceInfo.cId + " nolu cihaz bağlanamadı.");
                        }

                    }
                    catch
                    { }


                    if (client == null || !client.Connected)
                    {
                        try
                        {
                            if (client == null) Console.WriteLine(deviceInfo.cIP + " Cihaz dinlemede..");
                            client = new TcpClient();
                            ConnectionManager.ReturnValues values = fsm.PingAndPortTest(deviceInfo.cIP, deviceInfo.Port, client);

                            if (values != ConnectionManager.ReturnValues.Successful)
                                LogMessage(deviceInfo.cId, deviceInfo.cIP + "---" + deviceInfo.cId + " nolu cihaz bağlanamadı.Hata:" + values);
                            else
                                LogMessage(deviceInfo.cId, deviceInfo.cIP + "---" + deviceInfo.cId + " Cihaz dinleniyor.." + values);
                        }
                        catch (Exception ex)
                        {
                            LogMessage(deviceInfo.cId, deviceInfo.cIP + "---" + deviceInfo.cId + " nolu cihaz bağlanamadı.Hata:" + ex.Message);
                        }
                    }


                    Result = fsm.ListenOnlineRequest(out ID, out Address, out OfflineLogCount, out Emergency, out Door, out Kart, client, timeout, Cnv, out temp);
                    DateTime zaman = DateTime.Now;

                    //LogMessage(deviceInfo.cIP + "---" + deviceInfo.cId + " ListenOnlineRequest method response... "+ Result.ToString());

                    if (Kart == ConnectionManager.CardStatus.NoCard)
                    {

                        //if (list.Count > 0 && (list.LastOrDefault().CardID == cardId))
                        //{
                        //    controlZaman = DateTime.Now;
                        //    //Result.ToString()
                        //    insertDB(deviceInfo.cIP, list.LastOrDefault().CardID, zaman.AddSeconds(-waitTime), 2, "Completed","Successful" );
                        //    fsm.SendAccess(client, Address, ConnectionManager.AccessType.Deny, 0, ConnectionManager.BuzzerState.BuzzerOn, new byte[1], timeout, Cnv);
                        //    list.Clear();
                        //}

                        //LogMessage(deviceInfo.cIP + "---" + deviceInfo.cId + " cihaz NoCard Statusünde... ");

                        if (list.Count > 0 && (ID.ToString() == "0"))
                        {

                            insertDB(deviceInfo.cIP, list.LastOrDefault().CardID, zaman.AddSeconds(-waitTime), 2, "Completed", "Successful");
                            fsm.SendAccess(client, Address, ConnectionManager.AccessType.Deny, 0, ConnectionManager.BuzzerState.BuzzerOn, new byte[1], timeout, Cnv);
                            list.Clear();
                        }

                    }

                    if (Kart == ConnectionManager.CardStatus.Card)
                    {

                        //LogMessage(deviceInfo.cIP + "---" + deviceInfo.cId + " cihaz Card Statusünde... ");
                        CardInfo item = new CardInfo();
                        cardId = ID.ToString();
                        if (!checkDB(cardId, deviceInfo))
                        {
                            fsm.CardResponse(client, Address, ConnectionManager.CardStatus.NoCard, timeout, Cnv);
                            fsm.SendAccess(client, Address, ConnectionManager.AccessType.Deny, 0, ConnectionManager.BuzzerState.BuzzerOn, new byte[1], timeout, Cnv);
                            continue;
                        }
 
                        //Aktif kart yerine tanımsız koyduk... Aktif kart çıkış bilgisi yazacağız 5 sn geç yazıyordu.... Kontrol edilecek.... Yakup Bey bekliyoruz....
                        if (list.Count > 0 && (list.LastOrDefault().CardID != cardId))
                        {
                            insertDB(deviceInfo.cIP, list.LastOrDefault().CardID, zaman.AddSeconds(-waitTime), 2, "Completed", "Successful");
                            list.Clear();
                        }

                        //Aktif kart yerine tanımsız koyduk... Aktif kart çıkış bilgisi yazacağız 5 sn geç yazıyordu.... Kontrol edilecek.... Yakup Bey bekliyoruz....

                        else if (list.Count > 0 && (list.LastOrDefault().CardID == cardId && cardId != "0"))
                        {

                            list.LastOrDefault().TransactionDateTime = DateTime.Now;
                            fsm.CardResponse(client, Address, ConnectionManager.CardStatus.Card, timeout, Cnv);

                        }

                        else if (list.Count == 0 || cardId != "0")
                        {
                            item.CardID = cardId;
                            item.TransactionDateTime = DateTime.Now;
                            list.Add(item);
                            insertDB(deviceInfo.cIP, list.LastOrDefault().CardID, zaman, 1, "Started", "Successful");
                            fsm.CardResponse(client, Address, ConnectionManager.CardStatus.Card, timeout, Cnv);
                            fsm.SendAccess(client, Address, ConnectionManager.AccessType.Accept, 1, ConnectionManager.BuzzerState.BuzzerOn, new byte[1], timeout, Cnv);
                        }


                    }

                    if (!PingConnection(deviceInfo))
                        client.Close();
                }
                catch (Exception ex)
                {

                    LogMessage(string.Empty, "Hata:" + ex.Message + "---" + ex.InnerException);

                }

            }


        }

        public bool PingConnection(DeviceInfo deviceInfo)
        {
            bool pingable = false;
            Ping pinger = null;
            try
            {
                //TcpClient tc = new TcpClient(deviceInfo.cIP, deviceInfo.Port);
                //tc.Close();
                pinger = new Ping();
                PingReply reply = pinger.Send(deviceInfo.cIP);
                pingable = (reply.Status == IPStatus.Success);
                //return true;
                if (!pingable)
                    LogMessage(deviceInfo.cId, deviceInfo.cIP + " nolu cihaz bağlanamadı.Hata Network:" + reply.Status);

            }
            catch (PingException ex)
            {
                LogMessage(deviceInfo.cId, deviceInfo.cIP + " nolu cihaz bağlanamadı.Hata Network ex:" + ex.Message);
                return false;

            }
            finally
            {

                if (pinger != null)
                {
                    pinger.Dispose();
                }

            }


            return pingable;

        }


        protected override void OnStop()
        {
            LogMessage(string.Empty,"In OnStop.");
            // Update the service state to Stop Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOP_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            // Update the service state to Stopped.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

    }


    public static class UserTranslator
    {

        public class DeviceInfo
        {
            public string cIP { get; set; }

            public string cId { get; set; }

            public int Port { get; set; }

            //public TcpClient Client { get; set; }

            public bool Connected { get; set; }
        }

        public class CardInfo
        {

            public string CardID { get; set; }
            public DateTime TransactionDateTime { get; set; }

        }

        public static List<DeviceInfo> GetDevices(this SqlDataReader reader)
        {
            List<DeviceInfo> list = new List<DeviceInfo>();
            while (reader.Read())
            {
                DeviceInfo item = new DeviceInfo();
                item.cIP = SqlHelper.GetNullableString(reader, "IP_ADRESI");
                item.cId = SqlHelper.GetNullableString(reader, "CIHAZ_NO");
                item.Port = SqlHelper.GetNullableInt32(reader, "PORT");
                list.Add(item);
            }

            return list;
        }


    }
}
