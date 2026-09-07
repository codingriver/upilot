namespace CodingRiver.UPilot.Acceptance
{
    /// <summary>
    /// Compile-only probe used by the EditorExecutionState v2 reload acceptance matrix.
    /// Incrementing <see cref="Round"/> intentionally triggers one script compilation.
    /// </summary>
    internal static class UPilotEditorStateV2CompileProbe
    {
        internal const int Round = 12;
    }
}
