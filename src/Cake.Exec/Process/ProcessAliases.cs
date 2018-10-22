using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cake.Core;
using Cake.Core.Annotations;
using Cake.Core.Diagnostics;
using Cake.Core.IO;

namespace Cake.Exec.Process
{
    /// <summary>
    /// Contains functionality for running processes
    /// </summary>
    [CakeAliasCategory("Process")]
    public static class ProcessAliases
    {
        [CakeMethodAlias]
        public static ProcessWrapper Spawn(this ICakeContext context, FilePath executable, string args, IEnumerable<KeyValuePair<string, string>> environmentVariables = null, bool redirectStandardInput = false, bool redirectStandardOutput = false, bool redirectStandardError = false)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            
            var resolved = TryResolve(context, executable);
            var fileName = context.Environment.Platform.IsUnix() ? resolved.FullPath : resolved.FullPath.Quote();
            var workingDirectory = context.Environment.WorkingDirectory.MakeAbsolute(context.Environment).FullPath;
            var message = string.Concat(fileName, " ", args);
            context.Log.Verbose(Verbosity.Diagnostic, "Executing: {0}", message);
            return new ProcessWrapper(workingDirectory, fileName, args, environmentVariables, redirectStandardInput, redirectStandardOutput, redirectStandardError);
        }

        private static FilePath TryResolve(ICakeContext context, FilePath executable)
        {
            try
            {
                return context.Tools.Resolve(executable.FullPath) ?? context.Tools.Resolve(executable.FullPath + ".exe") ?? executable.FullPath;
            }
            catch (InvalidOperationException) // This is a bug in cake
            {
                return executable.FullPath;
            }
        }

        [CakeMethodAlias]
        public static int Exec(this ICakeContext context, FilePath executable, string args, IEnumerable<KeyValuePair<string, string>> environmentVariables = null, int[] validExitCodes = null)
        {
            using (var proc = Spawn(context, executable, args, environmentVariables))
            {
                if (validExitCodes != null)
                    proc.ValidExitCodes = validExitCodes;
                return proc.Task.Result;
            }
        }

        [CakeMethodAlias]
        public static string ExecOut(this ICakeContext context, FilePath executable, string args, IEnumerable<KeyValuePair<string, string>> environmentVariables = null, int[] validExitCodes = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var lines = ExecLines(context, executable, args, environmentVariables, validExitCodes);
            return string.Join(Environment.NewLine, lines);
        }

        [CakeMethodAlias]
        public static IEnumerable<string> ExecLines(this ICakeContext context, FilePath executable, string args, IEnumerable<KeyValuePair<string, string>> environmentVariables = null, int[] validExitCodes = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            using (var proc = Spawn(context, executable, args, environmentVariables, false, true))
            {
                if (validExitCodes != null && validExitCodes.Length > 0)
                    proc.ValidExitCodes = validExitCodes;
                var rdr = proc.Process.StandardOutput;
                while (true)
                {
                    var line = rdr.ReadLine();
                    if (line == null) break;
                    context.Log.Verbose(Verbosity.Diagnostic, line);
                    yield return line;
                }
            }
        }
    }

    public class ProcessWrapper : IDisposable
    {
        private readonly TaskCompletionSource<int> running = new TaskCompletionSource<int>();
        private System.Diagnostics.Process _process;
        public int[] ValidExitCodes { get; set; } = {0};

        public System.Diagnostics.Process Process => _process;
        public Task<int> Task => running.Task;
        public Stream StandardInputStream => Process.StandardInput.BaseStream;
        public StreamWriter StandardInput => Process.StandardInput;
        public Stream StandardOutputStream => Process.StandardOutput.BaseStream;
        public StreamReader StandardOutput => Process.StandardOutput;
        public Stream StandardErrorStream => Process.StandardError.BaseStream;
        public StreamReader StandardError => Process.StandardError;

        public void CopyTo(Stream destination)
        {
            StandardOutputStream.CopyTo(destination);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            return StandardOutputStream.Read(buffer, offset, count);
        }

        public string ReadAllText()
        {
            return StandardOutput.ReadToEnd();
        }

        public void WriteAllText(string s)
        {
            StandardInput.Write(s);
            StandardInput.Close();
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            StandardInputStream.Write(buffer, offset, count);
        }

        public ProcessWrapper(string workingDirectory, string fileName, string args, IEnumerable<KeyValuePair<string, string>> environmentVariables = null, bool redirectStandardInput = false, bool redirectStandardOutput = false, bool redirectStandardError = false)
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = redirectStandardInput,
                RedirectStandardOutput = redirectStandardOutput,
                RedirectStandardError = redirectStandardError,
                Arguments = args,
            };

            if (environmentVariables != null)
                foreach (var ev in environmentVariables)
                    psi.EnvironmentVariables[ev.Key] = ev.Value;

            _process = System.Diagnostics.Process.Start(psi);
            Debug.Assert(_process != null, "_process != null");
            _process.Exited += Process_Exited;
            _process.EnableRaisingEvents = true;
        }

        private void Process_Exited(object sender, EventArgs e)
        {
            running.SetResult(Process.ExitCode);
        }

        public void CheckExitCode()
        {
            var exitCode = Task.Result;
            var validExitCodes = ValidExitCodes ?? new[] {0};
            if (!validExitCodes.Any()) return;
            if (!validExitCodes.Contains(exitCode))
                throw new Exception("Process exited with " + exitCode);
        }

        public void Dispose()
        {
            if (_process == null) return;
            CheckExitCode();
            _process?.Dispose();
            _process = null;
        }
    }
}
