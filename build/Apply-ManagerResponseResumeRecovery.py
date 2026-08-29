from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)

root = Path.cwd()
policy = root / "src/PCCExecutive.Application/PrePlanAutoRecoveryPolicy.cs"
policy.write_text('''namespace PCCExecutive.Application;\n\npublic enum PrePlanAutoRecoveryMode { EvidenceRefresh, ExistingManagerResponse }\n\npublic static class PrePlanAutoRecoveryPolicy\n{\n    public static PrePlanAutoRecoveryMode Classify(string? runtimeErrorFingerprint)\n    {\n        if (string.IsNullOrWhiteSpace(runtimeErrorFingerprint)) return PrePlanAutoRecoveryMode.EvidenceRefresh;\n        return runtimeErrorFingerprint.Contains("MANAGER_PLAN_NOT_STRUCTURED", StringComparison.OrdinalIgnoreCase) ||\n               runtimeErrorFingerprint.Contains("Manager response rejected", StringComparison.OrdinalIgnoreCase)\n            ? PrePlanAutoRecoveryMode.ExistingManagerResponse\n            : PrePlanAutoRecoveryMode.EvidenceRefresh;\n    }\n}\n''', encoding="utf-8")

gateway_path = root / "src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs"
gateway = gateway_path.read_text(encoding="utf-8")
gateway = replace_once(gateway,
'''                            _autopilot = "RECOVERING";
                            _latestManagerHandoff = "RECOVERING_EVIDENCE — retrying the previous pre-plan infrastructure failure automatically.";''',
'''                            var prePlanRecovery = PrePlanAutoRecoveryPolicy.Classify(loop.RuntimeErrorFingerprint);
                            _autopilot = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse ? "PLANNING" : "RECOVERING";
                            _latestManagerHandoff = prePlanRecovery == PrePlanAutoRecoveryMode.ExistingManagerResponse
                                ? "RECOVERING_MANAGER_RESPONSE — reparsing the already-received Manager response with the current schema; no resend will occur."
                                : "RECOVERING_EVIDENCE — retrying the previous pre-plan infrastructure failure automatically.";''',
"pre-plan restart recovery routing")
gateway_path.write_text(gateway, encoding="utf-8")

test_path = root / "tests/PCCExecutive.Application.Tests/LoopGuardRestartAcceptanceTests.cs"
tests = test_path.read_text(encoding="utf-8")
anchor = '''    private static StagnationObservation Observation('''
insert = '''    [Theory]
    [InlineData("Manager response rejected: MANAGER_PLAN_NOT_STRUCTURED", PrePlanAutoRecoveryMode.ExistingManagerResponse)]
    [InlineData("MANAGER_PLAN_NOT_STRUCTURED: ExpectedRoutingIdentity", PrePlanAutoRecoveryMode.ExistingManagerResponse)]
    [InlineData("PCC_BRANCH_403", PrePlanAutoRecoveryMode.EvidenceRefresh)]
    public void Restart_reuses_received_manager_response_after_schema_failure(string fingerprint, PrePlanAutoRecoveryMode expected)
    {
        Assert.Equal(expected, PrePlanAutoRecoveryPolicy.Classify(fingerprint));
    }

'''
tests = replace_once(tests, anchor, insert + anchor, "restart schema recovery regression")
test_path.write_text(tests, encoding="utf-8")

print("Manager response resume recovery patch applied.")
