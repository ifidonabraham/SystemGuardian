using System;
using System.Threading;
using System.Threading.Tasks;
using SystemGuardian.Agents;

namespace SystemGuardian.App;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         SYSTEM GUARDIAN - Multi-Agent Resource Manager          ║");
        Console.WriteLine("║              v1.0 - Resource Protection System                  ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        try
        {
            Console.WriteLine("[STARTUP] Initializing agents...");

            var monitoringAgent    = new MonitoringAgent();
            var forecastingAgent   = new ForecastingAgent();
            var processTreeAgent   = new ProcessTreeAgent();
            var contextAgent       = new ContextAgent();
            var actionRankerAgent  = new ActionRankerAgent();
            var executionAgent     = new ExecutionAgent();
            var whitelistGuardAgent = new WhitelistGuardAgent();
            var loggerAgent        = new LoggerAgent();
            var uiAgent            = new UINotificationAgent();
            var feedbackAgent      = new FeedbackAgent();

            var orchestrator = new OrchestratorAgent(
                monitoringAgent, forecastingAgent, processTreeAgent, contextAgent,
                actionRankerAgent, executionAgent, whitelistGuardAgent, loggerAgent,
                uiAgent, feedbackAgent);

            await orchestrator.InitializeAsync();
            Console.WriteLine("✓ All agents initialized successfully");
            Console.WriteLine();

            Console.WriteLine("[ORCHESTRATOR] Starting system monitoring loop...");
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine(new string('─', 65));
            Console.WriteLine();

            int cycleCount = 0;
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var tickId   = Guid.NewGuid().ToString("N")[..12];
                    var decision = await orchestrator.RunCycleAsync(tickId);

                    Console.WriteLine();
                    Console.WriteLine($"Cycle #{++cycleCount} | Tick: {tickId} | Tier: {decision.CurrentTier}");
                    Console.WriteLine($"  Worst: {decision.WorstResource ?? "n/a"} ({decision.WorstResourcePct:F1}%)");
                    Console.WriteLine($"  Agents: [{string.Join(", ", decision.ActiveAgents)}]");

                    if (decision.ExecutedAction != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⚠ ACTION: {decision.ExecutedAction.ApprovedAction} on " +
                            $"{decision.ExecutedAction.TargetName} (PID {decision.ExecutedAction.TargetPid})");
                        Console.ResetColor();
                    }

                    if (decision.TierChanged)
                    {
                        Console.ForegroundColor = decision.CurrentTier >= 3 ? ConsoleColor.Red : ConsoleColor.Yellow;
                        Console.WriteLine($"  ⬆ Tier changed: {decision.PreviousTier} → {decision.CurrentTier}");
                        Console.ResetColor();
                    }

                    foreach (var reason in decision.DecisionReasons)
                        Console.WriteLine($"    • {reason}");

                    await Task.Delay(2000, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C
            }

            Console.WriteLine();
            Console.WriteLine("[SHUTDOWN] Stopping system...");
            Console.WriteLine($"✓ Ran {cycleCount} orchestration cycles");
            Console.WriteLine("✓ System Guardian stopped cleanly");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n✗ Fatal error: {ex.Message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }
}
