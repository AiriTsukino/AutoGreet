using System.Numerics;
using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class LogTab
{
    private readonly DiagnosticLogService logs;

    public LogTab(DiagnosticLogService logs)
    {
        this.logs = logs;
    }

    public void Draw()
    {
        UiHelpers.Section("Log");
        UiHelpers.TextDisabledWrapped("Macro syntax errors and queue-stopping messages appear here. AutoGreet pauses when unsupported macro syntax is found so you can fix the active greet before the queue continues.");

        if (ImGui.Button("Clear log"))
            logs.Clear();

        ImGui.SameLine();
        if (ImGui.Button("Copy supported syntax"))
            ImGui.SetClipboardText(DiagnosticLogService.SupportedSyntaxText);

        ImGui.Spacing();
        ImGui.TextWrapped(DiagnosticLogService.SupportedSyntaxText);
        ImGui.Separator();

        if (logs.Entries.Count == 0)
        {
            UiHelpers.TextDisabledWrapped("No log entries yet.");
            return;
        }

        if (ImGui.BeginTable("autogreet-log-table", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 128f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 74f);
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Macro / line", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableHeadersRow();

            foreach (var entry in logs.Entries)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled(entry.CreatedUtc.LocalDateTime.ToString("g"));

                ImGui.TableSetColumnIndex(1);
                DrawSeverity(entry.Severity);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextWrapped(entry.Title);

                ImGui.TableSetColumnIndex(3);
                if (!string.IsNullOrWhiteSpace(entry.MacroName))
                {
                    ImGui.TextWrapped(entry.MacroName);
                    if (entry.LineNumber > 0)
                        ImGui.TextDisabled($"Line {entry.LineNumber}: {entry.LineText}");
                }
                else
                {
                    ImGui.TextDisabled("None");
                }

                ImGui.TableSetColumnIndex(4);
                ImGui.TextWrapped(entry.Message);
            }

            ImGui.EndTable();
        }
    }

    private static void DrawSeverity(MacroLogSeverity severity)
    {
        var color = severity switch
        {
            MacroLogSeverity.Error => new Vector4(1f, 0.35f, 0.35f, 1f),
            MacroLogSeverity.Warning => new Vector4(1f, 0.85f, 0.35f, 1f),
            _ => new Vector4(0.75f, 0.85f, 1f, 1f),
        };

        ImGui.TextColored(color, severity.ToString());
    }
}
