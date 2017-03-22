using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Elcom.Utils
{
  internal class AdvancedTaskbar
  {
    private static uint maxRange;

    /// <summary>Taskbar progress function.</summary>
    private static class TaskbarProgress
    {
      /// <summary>State of progress.</summary>
      public enum eStates
      {
        /// <summary>Progress is disabled.</summary>
        Disabled = 0,
        /// <summary>Progress is disabled.</summary>
        NoProgress = 0,
        /// <summary>Processing; unknown position.</summary>
        Indeterminate = 0x1,
        /// <summary>Normal progress.</summary>
        Normal = 0x2,
        /// <summary>Processing ended with an error.</summary>
        Error = 0x4,
        /// <summary>Processing is paused.</summary>
        Paused = 0x8
      }

      /// <summary>Windows 7 taskbar (3) interface.</summary>
      [ComImport()]
      [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
      [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
      private interface ITaskbarList3
      {
        #region ITaskbarList
        [PreserveSig]
        void HrInit();
        [PreserveSig]
        void AddTab(IntPtr hwnd);
        [PreserveSig]
        void DeleteTab(IntPtr hwnd);
        [PreserveSig]
        void ActivateTab(IntPtr hwnd);
        [PreserveSig]
        void SetActiveAlt(IntPtr hwnd);
        #endregion

        #region ITaskbarList2
        [PreserveSig]
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullScreen);
        #endregion

        #region ITaskbarList3
        [PreserveSig]
        void SetProgressValue(IntPtr hwnd, UInt64 ullCompleted, UInt64 ullTotal);
        [PreserveSig]
        void SetProgressState(IntPtr hwnd, eStates state);
        #endregion
      }

      /// <summary>Windows 7 (Task bar 3) instance class.</summary>
      [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
      [ClassInterface(ClassInterfaceType.None)]
      [ComImport()]
      private sealed class TaskbarInstance { }

      /// <summary>Task bar instance.</summary>
      private static ITaskbarList3 taskbar;
      /// <summary>Supported flag.</summary>
      private static bool active;
      /// <summary>Main form to display progress.</summary>
      private static Form taskBarForm;

      /// <summary>Static constructor.</summary>
      static TaskbarProgress()
      {
        object instance = new TaskbarInstance();
        taskbar = (instance as ITaskbarList3);
        active = Environment.OSVersion.Version >= new Version(6, 1);
      }

      /// <summary>Invoke task bar method in respecting thread.</summary>
      /// <param name="method">Invoked method.</param>
      /// <returns>An System.IAsyncResult that represents the result of the
      /// System.Windows.Forms.Control.BeginInvoke(System.Delegate) operation.</returns>
      private static IAsyncResult FormInvoke(Delegate method)
      {
        if (object.ReferenceEquals(taskBarForm, null) || !active)
          return null;
        if (taskBarForm.Created)
          return taskBarForm.BeginInvoke(method);
        return null;
      }

      /// <summary>Initialise main task bar windows to display progress.</summary>
      /// <param name="form">Main task bar form.</param>
      public static void SetMainWindow(Form form) { taskBarForm = form; }

      /// <summary>Set progress state.</summary>
      /// <param name="window">User window handle.</param>
      /// <param name="state">State of taskbar.</param>
      public static void SetState(eStates state)
      {
        FormInvoke((MethodInvoker)delegate { taskbar.SetProgressState(taskBarForm.Handle, state); });
      }

      /// <summary>Set progress state.</summary>
      /// <param name="window">User window handle.</param>
      /// <param name="state">State of taskbar.</param>
      /// <param name="maxValue">Maximal progress position.</param>
      public static void SetState(eStates state, ulong maxValue)
      {
        FormInvoke((MethodInvoker)delegate
        {
          taskbar.SetProgressState(taskBarForm.Handle, state);
          taskbar.SetProgressValue(taskBarForm.Handle, 0, maxValue);
        });
      }

      /// <summary>Set progress position value.</summary>
      /// <param name="window">User window handle.</param>
      /// <param name="position">Current progress position.</param>
      /// <param name="maxValue">Maximal progress position.</param>
      public static void SetValue(ulong position, ulong maxValue)
      {
        FormInvoke((MethodInvoker)delegate { taskbar.SetProgressValue(taskBarForm.Handle, position, maxValue); });
      }
    }


    public static void Init(Form sourceForm)
    {
      TaskbarProgress.SetMainWindow(sourceForm);
    }

    public static void EnableItermediate()
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Indeterminate);
    }

    public static void EnableProgress(uint range)
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Normal);
      maxRange = range;
    }

    public static void SetPosition(uint position)
    {
      if (position > maxRange)
        EnableItermediate();
      else
        TaskbarProgress.SetValue(position, maxRange);
    }

    public static void Disable()
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Disabled);
    }

    public static void Pause()
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Paused);
    }

    public static void Continue()
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Normal);
    }

    public static void SetErrorState()
    {
      TaskbarProgress.SetState(TaskbarProgress.eStates.Error);
    }

    public static void SetUserResponseState(DialogResult state)
    {
      if (state == DialogResult.Abort)
        Disable();
      else if (state == DialogResult.None)
        Pause();
      else
        Continue();
    }
  }
}
