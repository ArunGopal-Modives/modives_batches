using System;
using System.IO;

namespace Services
{
    public static class AppLogger
    {
        private static readonly object Sync = new();

        private static string LogDirectory =>
            Path.Combine(AppContext.BaseDirectory, "logs");

        private static string LogFile =>
            Path.Combine(LogDirectory, $"StagingDataSync_{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Warning(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null)
        {
            var details = ex == null ? message : $"{message}{Environment.NewLine}{FormatException(ex)}";
            Write("ERROR", details);
        }

        public static void Success(string message) => Write("SUCCESS", message);

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                var line =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

                lock (Sync)
                {
                    File.AppendAllText(LogFile, line);
                }

                Console.WriteLine(line.TrimEnd());
            }
            catch
            {
                // Logging must never stop the staging process.
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [LOGGER_ERROR] {message}");
            }
        }

        private static string FormatException(Exception ex)
        {
            var result = ex.ToString();

            if (ex is MySqlConnector.MySqlException mysqlEx)
            {
                result +=
                    $"{Environment.NewLine}MySQL Error Number: {mysqlEx.Number}" +
                    $"{Environment.NewLine}MySQL SqlState: {mysqlEx.SqlState}" +
                    $"{Environment.NewLine}MySQL Error Code: {mysqlEx.ErrorCode}";
            }

            return result;
        }
    }
}
