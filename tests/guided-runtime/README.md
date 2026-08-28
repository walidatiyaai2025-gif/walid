# Guided-runtime acceptance

`acceptance-matrix.json` is the canonical, deterministic inventory for terminal
guided-runtime acceptance. It maps the 52 explicit GR-001 through GR-005 package
tests to three mandatory release-closure proofs: exact-source package provenance,
installed smoke, and complete evidence/blocker accounting.

Validate the inventory without claiming runtime success:

```powershell
./build/Test-GuidedRuntimeAcceptanceMatrix.ps1
```

The matrix is an inventory, not evidence. A case may only be reported as passed
after its real automated or manual evidence is captured against the accepted SHA.
