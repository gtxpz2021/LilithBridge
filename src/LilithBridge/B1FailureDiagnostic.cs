namespace LilithBridge;
internal sealed record NativeReadContextDiagnostic(bool Active,bool HasConfig,bool PlayerDirected,int CurrentSources,int SelectedSources);
internal sealed record B1FailureDiagnostic(string Phase,string Alias,string Kind,string Reason,string RequestId,
    NativeReadContextDiagnostic Capture,NativeReadContextDiagnostic Commit,bool ChoiceProvided,bool ChoiceNonNull);
