using UnityEngine;
using System.IO;
using System;
using Mono.CSharp;

public class LogReportPrinter : ReportPrinter
{
  static LogReportPrinter() { }

  private static readonly Type ErrorMessageType = typeof(Mono.CSharp.Evaluator).Assembly.GetType("Mono.CSharp.ErrorMessage");

  public override void Print(AbstractMessage msg, bool showFullPath)
  {
    if (msg.Code == 1685 || // Predefined type is defined multiple times...
        msg.Code == 433) // Imported type is defined multiple times...
      return; // Don't print this error, it is too noisy.

    StringWriter writer = new StringWriter();
    Print(msg, writer, showFullPath);
    string formattedMessage = writer.ToString();
    if (msg.GetType() == ErrorMessageType)
      Debug.LogError(formattedMessage);
    else if (msg.IsWarning)
      Debug.LogWarning(formattedMessage);
    else
      Debug.Log(formattedMessage);
  }
}
