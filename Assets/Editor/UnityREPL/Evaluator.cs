//-----------------------------------------------------------------
// Core evaluation loop, including environment handling for living in the Unity
// editor and dealing with its code reloading behaviors.
//-----------------------------------------------------------------
using UnityEngine;
using UnityEditor;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.IO;
using Mono.CSharp;

class EvaluationException : Exception {
}

class EvaluationHelper {
  public EvaluationHelper() {}

  // static string[] REQUIRED_ASSEMBLIES = {
  //   "UnityEditor.dll",
  //   "UnityEngine.dll",
  //   "mscorlib.dll",
  //   "System.dll",
  // };

  static string[] NAMESPACES = {
    "System",
    "System.IO",
    "System.Linq",
    "System.Collections",
    "System.Collections.Generic",
    "UnityEditor",
    "UnityEngine",
  };

  private static Mono.CSharp.Evaluator _evaluator;
  internal static Mono.CSharp.Evaluator EvaluatorInstance
  {
    get
    {
      if (_evaluator == null)
      {
        CompilerSettings settings = new CompilerSettings();
        // foreach (string asm in REQUIRED_ASSEMBLIES)
        //   settings.AssemblyReferences.Add(asm);

        /*
        We need to tell the evaluator to reference stuff we care about.  Since
        there's a lot of dynamically named stuff that we might want, we just pull
        the list of loaded assemblies and include them "all" (with the exception of
        a couple that I have a sneaking suspicion may be bad to reference -- noted
        below).

        Examples of what we might get when asking the current AppDomain for all
        assemblies (short names only):

        Stuff we avoid:
          UnityDomainLoad <-- Unity gubbins.  Probably want to avoid this.
          Mono.CSharp <-- The self-same package used to pull this off.  Probably
                          safe, but not taking any chances.
          interactive0 <-- Looks like what Mono.CSharp is making on the fly.  If we
                          load those, it APPEARS we may wind up holding onto them
                          'forever', so...  Don't even try.


        Mono runtime, which we probably get 'for free', but include just in case:
          System
          mscorlib

        Unity runtime, which we definitely want:
          UnityEditor
          UnityEngine

        The assemblies Unity generated from our project code now all begin with Assembly:
          Assembly-CSharp
          Assembly-CSharp-Editor
          ...
        */
        List<string> failedAssemblies = new List<string>();
        foreach (Assembly b in AppDomain.CurrentDomain.GetAssemblies())
        {
          string assemblyShortName = b.GetName().Name;
          if (!(assemblyShortName.StartsWith("Mono.CSharp") || assemblyShortName.StartsWith("UnityDomainLoad") || assemblyShortName.StartsWith("interactive")))
          {
            try
            {
              // _evaluator.ReferenceAssembly(b);
              settings.AssemblyReferences.Add(assemblyShortName);
            }
            catch (Exception)
            {
              failedAssemblies.Add(assemblyShortName);
            }
          }
        }

        if (failedAssemblies.Count > 0)
        {
          failedAssemblies.Sort();
          Debug.LogWarning("Failed to reference the following assemblies:\n  " + string.Join("\n  ", failedAssemblies));
        }

        _evaluator = new Mono.CSharp.Evaluator(new CompilerContext(settings, new LogReportPrinter()));

        // These won't work the first time through after an assembly reload.  No
        // clue why, but the Unity* namespaces don't get found.  Perhaps they're
        // being loaded into our AppDomain asynchronously and just aren't done yet?
        // Regardless, attempting to hit them early and then trying again later
        // seems to work fine.
        List<string> failedNamespaces = new List<string>();
        foreach (string ns in NAMESPACES)
        {
          try
          {
            _evaluator.Run("using " + ns + ";");
          }
          catch (Exception)
          {
            failedNamespaces.Add(ns);
          }
        }

        if (failedNamespaces.Count > 0)
        {
          failedNamespaces.Sort();
          Debug.LogWarning("Failed to reference the following namespaces:\n  " + string.Join("\n  ", failedNamespaces));
        }

        _evaluator.InteractiveBaseClass = typeof(UnityBaseClass);
      }

      return _evaluator;
    }
  }

  public bool Eval(string code)
  {
    EditorApplication.LockReloadAssemblies();

    bool status = false, hasOutput = false;
    object output = null;
    string res = null, tmpCode = code.Trim();
    // Debug.Log("Evaluating: " + tmpCode);

    try
    {
      if (tmpCode.StartsWith("="))
      {
        // Special case handling of calculator mode.  The problem is that
        // expressions involving multiplication are grammatically ambiguous
        // without a var declaration or some other grammatical construct.
        // TODO: Change the prompt in calculator mode.  Needs to be done from Shell.
        tmpCode = "(" + tmpCode.Substring(1, tmpCode.Length - 1) + ");";
      }
      res = Evaluate(tmpCode, out output, out hasOutput);
    }
    catch (EvaluationException)
    {
      output = null;
      hasOutput = false;
      res = tmpCode; // Enable continued editing on syntax errors, etc.
    }
    catch (Exception e)
    {
      Debug.LogError(e);

      res = tmpCode; // Enable continued editing on unexpected errors.
    }
    finally
    {
      status = res == null;
    }

    if (hasOutput)
    {
      if (status)
      {
        try
        {
          StringBuilder sb = new StringBuilder();
          PrettyPrint.PP(sb, output, true);
          Debug.Log(sb.ToString());
        }
        catch (Exception e)
        {
          Debug.LogError(e.ToString().Trim());
        }
      }
    }

    EditorApplication.UnlockReloadAssemblies();
    return status;
  }

  /* Copy-pasta'd from the DLL to try and differentiate between kinds of failure mode. */
  private string Evaluate(string input, out object result, out bool result_set) {
    result_set = false;
    result = null;

    CompiledMethod compiledMethod;
    string remainder = null;
    remainder = EvaluatorInstance.Compile(input, out compiledMethod);
    if(remainder != null)
      return remainder;

    if (compiledMethod == null)
      throw new EvaluationException();

    object typeFromHandle = null;
    try {
      EvaluatorProxy.InvokeThread = Thread.CurrentThread;
      EvaluatorProxy.Invoking     = true;
      compiledMethod(ref typeFromHandle);
    } catch(ThreadAbortException arg) {
      Thread.ResetAbort();
      Debug.LogError("Interrupted!\n" + arg.ToString());
      // TODO: How best to handle this?
    } finally {
      EvaluatorProxy.Invoking = false;
    }
    if (typeFromHandle != null)
    {
      result_set = true;
      result = typeFromHandle;
    }
    return null;
  }
}

// WARNING: Absolutely NOT thread-safe!
internal class EvaluatorProxy : ReflectionProxy {
  private static readonly Type EvaluatorType = typeof(Mono.CSharp.Evaluator);

  private static FieldInfo _invoke_thread;
  private static FieldInfo invoke_thread
  {
    get
    {
      if (_invoke_thread == null)
      {
        _invoke_thread = EvaluatorType.GetField("invoke_thread", NONPUBLIC_STATIC);
        if (_invoke_thread == null)
        {
          Debug.LogError("EvaluatorProxy.InvokeThreadField is null!  This is a bug in UnityREPL.");
          return null;
        }
      }
      return _invoke_thread;
    }
  }

  private static FieldInfo _invoking = EvaluatorType.GetField("invoking", NONPUBLIC_STATIC);
  private static FieldInfo invoking
  {
    get
    {
      if (_invoking == null)
      {
        _invoking = EvaluatorType.GetField("invoking", NONPUBLIC_STATIC);
        if (_invoking == null)
        {
          Debug.LogError("EvaluatorProxy.InvokingField is null!  This is a bug in UnityREPL.");
          return null;
        }
      }
      return _invoking;
    }
  }

  private static FieldInfo _fields;
  internal static Dictionary<string, Tuple<FieldSpec, FieldInfo>> Fields
  {
    get
    {
      if (_fields == null)
      {
        _fields = EvaluatorType.GetField("fields", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (_fields == null)
        {
          Debug.LogError("EvaluatorProxy.fields is null!  This is a bug in UnityREPL.");
          return new Dictionary<string, Tuple<FieldSpec, FieldInfo>>();
        }
      }
      // EvaluationHelper.EvaluatorInstance
      // EvaluatorType
      return (Dictionary<string, Tuple<FieldSpec, FieldInfo>>)_fields.GetValue(EvaluationHelper.EvaluatorInstance);
    }
  }

  internal static Thread InvokeThread
  {
    get { return (Thread)invoke_thread.GetValue(EvaluatorType); }
    set { invoke_thread.SetValue(EvaluatorType, value); }
  }

  internal static bool Invoking
  {
    get { return (bool)invoking.GetValue(EvaluatorType); }
    set { invoking.SetValue(EvaluatorType, value); }
  }
}

// Dummy class so we can output a string and bypass pretty-printing of it.
public struct REPLMessage {
  public string msg;

  public REPLMessage(string m) {
    msg = m;
  }
}

public class UnityBaseClass {
  private static readonly REPLMessage _help = new REPLMessage(@"UnityREPL v." + Shell.VERSION + @":

help;     -- This screen; help for helper commands.  Click the '?' icon on the toolbar for more comprehensive help.
vars;     -- Show the variables you've created this session, and their current values.
");

  public static REPLMessage help { get { return _help; } }

  public static REPLMessage vars {
    get {
      var fields  = EvaluatorProxy.Fields;
      StringBuilder tmp = new StringBuilder();
      // TODO: Sort this list...
      foreach(var kvp in fields) {
        Tuple<FieldSpec, FieldInfo> val = kvp.Value;
        FieldInfo field = (FieldInfo)val.Item2;
        tmp
          .Append(field.FieldType.FullName)
          .Append(" ")
          .Append(kvp.Key)
          .Append(" = ");
        PrettyPrint.PP(tmp, field.GetValue(null));
        tmp.Append(";\n");
      }
      return new REPLMessage(tmp.ToString());
    }
  }
}
