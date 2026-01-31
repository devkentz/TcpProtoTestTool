using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ProtoTestTool.Services;

public class ScriptDebugger
{
    private readonly HashSet<int> _breakpoints = [];
    private readonly SemaphoreSlim _pauseSignal = new(0, 1);
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public event Action<int, List<VariableInfo>>? BreakpointHit;
    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorOccurred;
    public event Action? ExecutionCompleted;

    public void AddBreakpoint(int line) => _breakpoints.Add(line);
    public void RemoveBreakpoint(int line) => _breakpoints.Remove(line);
    public void ClearBreakpoints() => _breakpoints.Clear();
    public IReadOnlySet<int> Breakpoints => _breakpoints;

    public async Task ExecuteWithDebuggerAsync(string code, ScriptOptions? baseOptions = null)
    {
        if (IsRunning) return;

        IsRunning = true;
        _cts = new CancellationTokenSource();

        var options = (baseOptions ?? ScriptOptions.Default)
            .AddReferences(typeof(Console).Assembly)
            .AddReferences(typeof(Enumerable).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "System.Text");

        var lines = code.Split('\n');
        ScriptState<object>? state = null;

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            for (int i = 0; i < lines.Length; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var lineNumber = i + 1;
                var line = lines[i].TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//"))
                    continue;

                // Check breakpoint
                if (_breakpoints.Contains(lineNumber))
                {
                    IsPaused = true;
                    var vars = CollectVariables(state);
                    FlushOutput(sw);
                    BreakpointHit?.Invoke(lineNumber, vars);

                    // Wait for continue/stop signal
                    await _pauseSignal.WaitAsync(_cts.Token);
                    IsPaused = false;
                }

                // Execute line
                try
                {
                    if (state == null)
                        state = await CSharpScript.RunAsync(line, options, cancellationToken: _cts.Token);
                    else
                        state = await state.ContinueWithAsync(line, cancellationToken: _cts.Token);

                    FlushOutput(sw);
                }
                catch (CompilationErrorException)
                {
                    // Skip lines that don't compile standalone (e.g., closing braces, partial statements)
                    // In a real debugger, we'd accumulate multi-line statements
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ErrorOccurred?.Invoke($"Line {lineNumber}: {ex.Message}");
                }
            }

            FlushOutput(sw);

            if (state?.ReturnValue != null)
                OutputReceived?.Invoke($"Result: {state.ReturnValue}");
        }
        catch (OperationCanceledException)
        {
            OutputReceived?.Invoke("[Stopped]");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            Console.SetOut(originalOut);
            IsRunning = false;
            IsPaused = false;
            ExecutionCompleted?.Invoke();
        }
    }

    public void Continue()
    {
        if (IsPaused && _pauseSignal.CurrentCount == 0)
            _pauseSignal.Release();
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (IsPaused && _pauseSignal.CurrentCount == 0)
            _pauseSignal.Release();
    }

    private void FlushOutput(StringWriter sw)
    {
        var output = sw.ToString();
        if (!string.IsNullOrEmpty(output))
        {
            OutputReceived?.Invoke(output);
            sw.GetStringBuilder().Clear();
        }
    }

    private static List<VariableInfo> CollectVariables(ScriptState<object>? state)
    {
        var vars = new List<VariableInfo>();
        if (state == null) return vars;

        foreach (var variable in state.Variables)
        {
            vars.Add(new VariableInfo
            {
                Name = variable.Name,
                Value = variable.Value?.ToString() ?? "null",
                Type = variable.Type.Name
            });
        }

        return vars;
    }
}

public class VariableInfo
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Type { get; set; } = "";
}
