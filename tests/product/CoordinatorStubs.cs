// Native/transport boundaries for exercising the real Coordinator without display writes.
using System;
using System.Collections.Generic;
using System.Threading;
namespace System.Windows.Forms { public static class Application { public static string ExecutablePath { get { return "not-used"; } } } }
namespace SwitchDesk {
    public class DisplayInfo { public string Id, Name; public InputValue DdcInput; }
    public class InputValue { public int? Value; }
    public class ControlResult { public bool Success, WriteReturned, ReadbackOk, ReadbackUnconfirmed; public int? Before, Readback; public string Detail; public long TotalMs, DiscoveryMs, ReadBeforeMs, WriteMs, ReadbackMs; public int ReadbackAttempts; }
}
namespace SwitchDesk.Desktop {
    internal class Session {
        public bool Connected;
        public Action OnConnected, OnDisconnected, OnHeartbeat;
        public Action<Dictionary<string,object>> OnControl;
        public Dictionary<string,object> LastState;
        public void SendControl(Dictionary<string,object> value) { if (Wire.S(value,"type") == "state") LastState = value; }
        public void Capabilities(bool value) { if (OnControl != null) OnControl(Wire.Obj("type","capabilities","input",value)); }
    }
    internal class InputEngine {
        public Action<Dictionary<string,object>> Send;
        public Action Switch, Emergency;
        public bool Forward, Receive;
        public void Configure(int epoch, bool forward, bool receive) { Forward=forward; Receive=receive; }
        public void Inject(Dictionary<string,object> value, long epoch) { }
        public void Heartbeat() { }
    }
    internal static class Wire {
        public static Dictionary<string,object> Obj(params object[] parts) { var d=new Dictionary<string,object>(); for(int i=0;i<parts.Length;i+=2) d[(string)parts[i]]=parts[i+1]; return d; }
        public static string S(IDictionary<string,object> d,string k) { object v; return d.TryGetValue(k,out v)?v as string:null; }
        public static bool B(IDictionary<string,object> d,string k) { object v; return d.TryGetValue(k,out v)&&v is bool&&(bool)v; }
        public static long L(IDictionary<string,object> d,string k,long fallback) { object v; return d.TryGetValue(k,out v)?Convert.ToInt64(v):fallback; }
        public static Dictionary<string,object> D(IDictionary<string,object> d,string k) { object v; return d.TryGetValue(k,out v)?v as Dictionary<string,object>:null; }
    }
    internal class WorkerReply { public bool Ok, RejectedBeforeWrite; public SwitchDesk.ControlResult Control; }
    internal class RunReply { public bool Started=true, TimedOut, CleanupUncertain; public WorkerReply Worker; }
    internal class TrialReply { public string RunId; public bool CleanupUncertain; public WorkerReply Target, Return; }
    internal static class ProcessRunner {
        public static bool WriteBlocked, Fail;
        public static int Calls, Writes, Readback = 17;
        public static Action BeforeRead, BeforeWrite;
        public static bool RejectWrite, UncertainCleanup, WorkerError, Timeout;
        public static List<int> WrittenInputs = new List<int>();
        public static string Arguments(params string[] args) { return ""; }
        public static RunReply Run(int timeout,bool write,params string[] args) {
            Interlocked.Increment(ref Calls);
            if (write) { Interlocked.Increment(ref Writes); lock (WrittenInputs) WrittenInputs.Add(Int32.Parse(args[5])); if (BeforeWrite != null) BeforeWrite(); }
            if (!write && BeforeRead != null) BeforeRead();
            int value=args[1]=="set"?Int32.Parse(args[5]):Readback;
            return new RunReply { TimedOut=Timeout, CleanupUncertain=UncertainCleanup, Worker = new WorkerReply { Ok=!Fail, RejectedBeforeWrite=RejectWrite, Control=new SwitchDesk.ControlResult { Success=!Fail, WriteReturned=!RejectWrite, Before=Readback, ReadbackOk=!Fail, ReadbackUnconfirmed=Fail&&!RejectWrite&&!WorkerError, Readback=Fail?(int?)null:value, Detail=Fail?"test readback unavailable":"readback verified" } } };
        }
    }
    internal class FakeJson { public T Deserialize<T>(string s) where T:class { return null; } }
    internal static class Program { public static readonly FakeJson Json=new FakeJson(); }
    public static class CoordinatorHarness {
        public static Dictionary<string,object> ReconcileInput(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=17; ProcessRunner.WriteBlocked=false;
            ProcessRunner.Fail=false; ProcessRunner.RejectWrite=false; ProcessRunner.UncertainCleanup=false; ProcessRunner.WorkerError=false; ProcessRunner.Timeout=false;
            ProcessRunner.BeforeRead=null; ProcessRunner.BeforeWrite=null; ProcessRunner.WrittenInputs.Clear();
            var session=new Session { Connected=true }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            c.Mapping.RecordTrial(17,17,true,true); c.ConfirmTrial();
            c.SetWindowsInput(true); session.Capabilities(mode!="no_permission");
            c.SetSource(mode=="mac_source"?"mac":"windows"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            ProcessRunner.Fail=true;
            if(mode=="permission_during_write") ProcessRunner.BeforeWrite=delegate { session.Capabilities(false); session.Capabilities(true); };
            c.RequestSwitch("mac"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            bool pendingArmed=c.Armed;
            string pendingTarget=c.Target;
            ProcessRunner.BeforeWrite=null;
            ProcessRunner.Fail=false; ProcessRunner.Readback=18;
            if(mode=="permission_after_write") { session.Capabilities(false); session.Capabilities(true); }
            if(mode=="windows_permission") { c.SetWindowsInput(false); c.SetWindowsInput(true); }
            if(mode=="reconnect") { session.Connected=false; session.OnDisconnected(); session.Connected=true; session.OnConnected(); session.Capabilities(true); }
            if(mode=="disconnect") { session.Connected=false; session.OnDisconnected(); }
            int readsBefore=ProcessRunner.Calls;
            SpinWait.SpinUntil(()=>c.Target=="mac" && !c.DisplayOperationBusy,5000);
            bool armed=c.Armed, forward=input.Forward, receive=input.Receive;
            var state=session.LastState;
            int reads=ProcessRunner.Calls-readsBefore;
            if(mode=="round_trip" || mode=="mac_source") {
                ProcessRunner.Fail=true; c.RequestSwitch("windows"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
                ProcessRunner.Fail=false; ProcessRunner.Readback=17;
                SpinWait.SpinUntil(()=>c.Target=="windows" && !c.DisplayOperationBusy,5000);
            }
            return Wire.Obj("pending_target",pendingTarget,"pending_armed",pendingArmed,"armed",armed,"forward",forward,"receive",receive,"state",state,
                "source",c.Source,"reads",reads,"final_target",c.Target,"final_armed",c.Armed,"final_forward",input.Forward,"final_receive",input.Receive,"writes",ProcessRunner.WrittenInputs.ToArray());
        }
        public static Dictionary<string,object> ReconcileRecovery(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=17; ProcessRunner.WriteBlocked=false;
            ProcessRunner.Fail=false; ProcessRunner.RejectWrite=false; ProcessRunner.UncertainCleanup=false; ProcessRunner.WorkerError=false; ProcessRunner.Timeout=false;
            ProcessRunner.BeforeRead=null; ProcessRunner.BeforeWrite=null; ProcessRunner.WrittenInputs.Clear();
            var session=new Session { Connected=true }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            c.Mapping.RecordTrial(17,17,true,true); c.ConfirmTrial();
            c.SetWindowsInput(true); session.Capabilities(true);
            c.RequestSwitch("mac"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            ProcessRunner.Fail=true; c.RestoreWindows(); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            string failedTarget=c.Target;
            int before=ProcessRunner.Calls;
            ProcessRunner.Fail=mode=="read_failure";
            ProcessRunner.Readback=mode=="wrong_input"?18:17;
            if(mode=="missing_permission") session.Capabilities(false);
            if(mode=="disconnect") { session.Connected=false; session.OnDisconnected(); }
            if(mode=="selection_changed") ProcessRunner.BeforeRead=delegate { c.InvalidateMapping(); };
            if(mode=="blocked") ProcessRunner.WriteBlocked=true;
            // Production schedules follow-up reads after the failed write. An older
            // Coordinator has no reconciler, so record its unchanged state immediately.
            bool hasReconciler=typeof(Coordinator).GetMethod("ReconcileDisplayAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!=null;
            if(hasReconciler) {
                int expectedReads=(mode=="wrong_input"||mode=="read_failure")?3:1;
                if(mode!="blocked") SpinWait.SpinUntil(()=>c.Target=="windows" || ProcessRunner.Calls-before>=expectedReads,6000);
                SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,3000);
            }
            string after=c.Target; bool confirmed=c.Mapping.UserConfirmed, ready=c.Ready, armed=c.Armed;
            var state=session.LastState;
            int reads=ProcessRunner.Calls-before, writesBefore=ProcessRunner.Writes;
            ProcessRunner.BeforeRead=null;
            if(mode=="success") { c.RequestSwitch(null); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000); }
            return Wire.Obj("initial_target",failedTarget,"target",after,"confirmed",confirmed,"ready",ready,"armed",armed,
                "reads",reads,"writes_before_next",writesBefore,"writes",ProcessRunner.WrittenInputs.ToArray(),
                "next_target",c.Target,"state",state);
        }
        public static Dictionary<string,object> VisualConfirmation(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=17; ProcessRunner.WriteBlocked=false;
            ProcessRunner.Fail=false; ProcessRunner.RejectWrite=false; ProcessRunner.UncertainCleanup=false; ProcessRunner.WorkerError=false; ProcessRunner.Timeout=false;
            ProcessRunner.BeforeRead=null; ProcessRunner.BeforeWrite=null; ProcessRunner.WrittenInputs.Clear();
            var session=new Session { Connected=true }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            if(mode!="unconfirmed") { c.Mapping.RecordTrial(17,17,true,true); c.ConfirmTrial(); }
            c.SetWindowsInput(true); session.Capabilities(mode!="missing_permission");
            ProcessRunner.Fail=true; ProcessRunner.RejectWrite=mode=="no_write"; ProcessRunner.UncertainCleanup=mode=="cleanup"; ProcessRunner.WorkerError=mode=="worker_error"; ProcessRunner.Timeout=mode=="timed_out";
            c.RequestSwitch("mac"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            var offered=session.LastState;
            long operation=Wire.L(offered,"visual_confirmation_id",0);
            if(mode=="new_operation") { c.RestoreWindows(); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000); }
            if(mode=="disconnect") { session.Connected=false; session.OnDisconnected(); }
            if(mode=="display_change") c.DisplayConfigurationChanged();
            if(mode=="new_selection") c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="other",Name="Other",DdcInput=new SwitchDesk.InputValue { Value=17 } },15);
            if(mode=="expired") {
                var f=typeof(Coordinator).GetField("pendingVisualUntil",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                if(f!=null) f.SetValue(c,DateTime.UtcNow.AddSeconds(-1));
            }
            if(mode=="blocked") ProcessRunner.WriteBlocked=true;
            session.OnControl(Wire.Obj("type","display_observed","target",mode=="wrong_target"?"windows":"mac","operation",mode=="wrong_operation"?operation+1:operation));
            bool readyAfter=c.Ready, armedAfter=c.Armed;
            int calls=ProcessRunner.Calls;
            c.SetSource("windows"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            if(mode=="replayed") { c.DisplayConfigurationChanged(); session.OnControl(Wire.Obj("type","display_observed","target","mac","operation",operation)); }
            return Wire.Obj("offered",Wire.S(offered,"pending_visual_target"),"id",operation,"target",c.Target,
                "ready_after_confirmation",readyAfter,"armed_after_confirmation",armedAfter,"armed",c.Armed,
                "input_active",input.Forward||input.Receive,"calls_after_confirmation",ProcessRunner.Calls-calls,"writes",ProcessRunner.WrittenInputs.ToArray());
        }
        public static Dictionary<string,object> TrialRecovery(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=17; ProcessRunner.WriteBlocked=false;
            ProcessRunner.Fail=false; ProcessRunner.BeforeRead=null; ProcessRunner.BeforeWrite=null; ProcessRunner.WrittenInputs.Clear();
            var session=new Session { Connected=true }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            int generation=(int)typeof(Coordinator).GetField("mappingGeneration",flags).GetValue(c);
            var read=new RunReply { Worker=new WorkerReply { Ok=mode!="read_failed",Control=new SwitchDesk.ControlResult { Readback=mode=="wrong_input"?15:17 } } };
            if(mode=="stale_selection") c.InvalidateMapping();
            var remember=typeof(Coordinator).GetMethod("RememberTrialRecovery",flags);
            bool accepted=remember!=null && (bool)remember.Invoke(c,new object[] { read,generation,"display","AOC",17,18 });
            if(mode=="display_event") c.DisplayConfigurationChanged();
            if(mode=="new_selection") c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="other",Name="Other",DdcInput=new SwitchDesk.InputValue { Value=17 } },15);
            bool offered=c.CanRecoverWindows;
            bool signaled=false;
            if(mode=="active_trial") {
                string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"switchdesk-trial-test-"+Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(directory);
                try {
                    typeof(Coordinator).GetField("trialDirectory",flags).SetValue(c,directory);
                    typeof(Coordinator).GetField("trialRunId",flags).SetValue(c,"test-run");
                    typeof(Coordinator).GetField("target",flags).SetValue(c,"unknown");
                    c.RequestSwitch(null);
                    string signal=System.IO.Path.Combine(directory,"restore");
                    signaled=System.IO.File.Exists(signal)&&System.IO.File.ReadAllText(signal)=="test-run";
                } finally {
                    typeof(Coordinator).GetField("trialDirectory",flags).SetValue(c,null);
                    typeof(Coordinator).GetField("trialRunId",flags).SetValue(c,null);
                    System.IO.Directory.Delete(directory,true);
                }
            } else { c.RestoreWindows(); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000); }
            bool confirmed=c.Mapping.UserConfirmed;
            c.RequestSwitch("mac"); SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            return Wire.Obj("accepted",accepted,"offered",offered,"writes",ProcessRunner.WrittenInputs.ToArray(),
                "target",c.Target,"confirmed",confirmed,"input_active",input.Forward||input.Receive,"armed",c.Armed,"signaled",signaled);
        }
        public static Dictionary<string,object> Recovery(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=17; ProcessRunner.WriteBlocked=false;
            ProcessRunner.Fail=false; ProcessRunner.BeforeRead=null; ProcessRunner.BeforeWrite=null; ProcessRunner.WrittenInputs.Clear();
            var session=new Session { Connected=true }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            if (mode!="never_confirmed") { c.Mapping.RecordTrial(17,17,true,true); c.ConfirmTrial(); }
            if (mode=="restart_mac" || mode=="restart_wrong_input") {
                c.InvalidateMapping();
                var restore=typeof(Coordinator).GetMethod("RestoreSavedDisplay",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                if(restore!=null) restore.Invoke(c,new object[] { new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=mode=="restart_mac"?18:15 } },17,18 });
            }
            if (mode=="permission_loss") {
                c.SetWindowsInput(true); session.Capabilities(true);
                ProcessRunner.BeforeWrite=delegate { session.Capabilities(false); };
            }
            if (mode=="disconnect") ProcessRunner.BeforeWrite=delegate { session.Connected=false; session.OnDisconnected(); };
            if (mode=="reconnect") ProcessRunner.BeforeWrite=delegate { session.OnConnected(); };
            if (mode=="readback_failure" || mode=="recovery_failure" || mode=="blocked" || mode=="no_mac_retry" || mode=="recovery_message") ProcessRunner.Fail=true;
            if (mode!="explicit_restore" && mode!="restart_mac" && mode!="restart_wrong_input") c.RequestSwitch("mac");
            SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            ProcessRunner.BeforeWrite=null;
            if (mode=="display_event") {
                var changed=typeof(Coordinator).GetMethod("DisplayConfigurationChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                if(changed==null) c.InvalidateMapping(); else changed.Invoke(c,null);
            }
            if (mode=="selection_changed") c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="other",Name="Other",DdcInput=new SwitchDesk.InputValue { Value=17 } },15);
            ProcessRunner.WriteBlocked=mode=="blocked";
            bool offered=c.CanRequestSwitch;
            var before=session.LastState;
            ProcessRunner.Fail=mode=="recovery_failure";
            if (mode=="explicit_restore" || mode=="recovery_message") session.OnControl(Wire.Obj("type","restore_windows_request"));
            else c.RequestSwitch(mode=="no_mac_retry"?"mac":"windows");
            SpinWait.SpinUntil(()=>!c.DisplayOperationBusy,2000);
            return Wire.Obj("offered",offered,"before",before,"target",c.Target,"state",session.LastState,
                "writes",ProcessRunner.WrittenInputs.ToArray(),"input_active",input.Forward||input.Receive,"armed",c.Armed,"busy",c.DisplayOperationBusy);
        }
        public static Dictionary<string,object> ConfirmObservation(string mode) {
            ProcessRunner.Calls=0; ProcessRunner.Writes=0; ProcessRunner.Readback=mode=="wrong_input"?18:17;
            ProcessRunner.WriteBlocked=mode=="blocked"; ProcessRunner.Fail=mode=="read_failed"; ProcessRunner.BeforeRead=null;
            var session=new Session { Connected=false }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            if (mode=="selection_changed") ProcessRunner.BeforeRead=delegate { c.InvalidateMapping(); };
            if (mode=="reconnected") ProcessRunner.BeforeRead=delegate { session.Connected=true; session.OnConnected(); };
            if (mode=="read_exception") ProcessRunner.BeforeRead=delegate { throw new InvalidOperationException("test read failure"); };
            if (mode=="no_selection") c.InvalidateMapping();
            var method=typeof(Coordinator).GetMethod("ConfirmObservedAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            // The old UI calls ConfirmTrial directly and cannot accept observed video without an automatic trial result.
            if (method==null) c.ConfirmTrial();
            else ((System.Threading.Tasks.Task)method.Invoke(c,null)).GetAwaiter().GetResult();
            ProcessRunner.BeforeRead=null;
            return Wire.Obj("confirmed",c.Mapping.UserConfirmed,"can_switch",c.CanRequestSwitch,"calls",ProcessRunner.Calls,
                "writes",ProcessRunner.Writes,"busy",c.Mapping.Busy,"input_active",input.Forward||input.Receive,"detail",c.Detail);
        }
        public static Dictionary<string,object> Run(bool connected,bool windowsInput,bool macInput,bool confirmed,bool fail) {
            ProcessRunner.Calls=0; ProcessRunner.WriteBlocked=false; ProcessRunner.Fail=fail;
            var session=new Session { Connected=connected }; var input=new InputEngine(); var c=new Coordinator(session,input);
            c.ConfigureDisplay(new SwitchDesk.DisplayInfo { Id="display",Name="AOC",DdcInput=new SwitchDesk.InputValue { Value=17 } },18);
            if(confirmed) { c.Mapping.RecordTrial(17,17,true,true); c.ConfirmTrial(); }
            c.SetWindowsInput(windowsInput); session.Capabilities(macInput);
            bool can=c.CanRequestSwitch; c.RequestSwitch("mac");
            SpinWait.SpinUntil(()=>ProcessRunner.Calls>0 || !can,1000);
            SpinWait.SpinUntil(()=>c.Target!="windows" || ProcessRunner.Calls==0,1000);
            return Wire.Obj("can_switch",can,"calls",ProcessRunner.Calls,"target",c.Target,"ready",c.Ready,"armed",c.Armed,
                "input_active",input.Forward||input.Receive,"state",session.LastState);
        }
    }
}
