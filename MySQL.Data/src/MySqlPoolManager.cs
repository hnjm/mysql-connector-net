// Copyright (c) 2004, 2019, Oracle and/or its affiliates. All rights reserved.
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License, version 2.0, as
// published by the Free Software Foundation.
//
// This program is also distributed with certain software (including
// but not limited to OpenSSL) that is licensed under separate terms,
// as designated in a particular file or component or in included license
// documentation.  The authors of MySQL hereby grant you an
// additional permission to link the program and your derivative works
// with the separately licensed software that they have included with
// MySQL.
//
// Without limiting anything contained in the foregoing, this file,
// which is part of MySQL Connector/NET, is also subject to the
// Universal FOSS Exception, version 1.0, a copy of which can be found at
// http://oss.oracle.com/licenses/universal-foss-exception.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License, version 2.0, for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using MySql.Data.Failover;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;


namespace MySql.Data.MySqlClient
{
  /// <summary>
  /// Summary description for MySqlPoolManager.
  /// </summary>
  internal class MySqlPoolManager
  {
    private static readonly Dictionary<string, MySqlPool> Pools = new Dictionary<string, MySqlPool>();
    private static readonly List<MySqlPool> ClearingPools = new List<MySqlPool>();
    internal static ConcurrentQueue<FailoverServer> _demotedHosts;
    internal static List<FailoverServer> _hosts;
    internal static int _demotedTimeout = 120000;
    internal static Timer _demotedServersTimer;

    // Timeout in seconds, after which an unused (idle) connection 
    // should be closed.
    internal static int maxConnectionIdleTime = 180;

    static MySqlPoolManager()
    {
      AppDomain.CurrentDomain.ProcessExit += EnsureClearingPools;
      AppDomain.CurrentDomain.DomainUnload += EnsureClearingPools;
    }

    private static void EnsureClearingPools(object sender, EventArgs e)
    {
      ClearAllPools();
    }

    // we add a small amount to the due time to let the cleanup detect
    //expired connections in the first cleanup.
    private static Timer timer = new Timer(CleanIdleConnections,
      null, (maxConnectionIdleTime * 1000) + 8000, maxConnectionIdleTime * 1000);

    private static string GetKey(MySqlConnectionStringBuilder settings)
    {
      string key = "";
      lock (settings)
      {
        key = settings.ConnectionString;
      }

      if (!settings.IntegratedSecurity || settings.ConnectionReset) return key;

      try
      {
        // Append SID to the connection string to generate a key
        // With Integrated security different Windows users with the same
        // connection string may be mapped to different MySQL accounts.
        System.Security.Principal.WindowsIdentity id =
          System.Security.Principal.WindowsIdentity.GetCurrent();

        key += ";" + id.User;
      }
      catch (System.Security.SecurityException ex)
      {
        // Documentation for WindowsIdentity.GetCurrent() states 
        // SecurityException can be thrown. In this case the 
        // connection can only be pooled if reset is done.
        throw new MySqlException(Resources.NoWindowsIdentity, ex);
      }

      return key;
    }
    public static MySqlPool GetPool(MySqlConnectionStringBuilder settings)
    {
      string text = GetKey(settings);

      lock (Pools)
      {
        MySqlPool pool;
        Pools.TryGetValue(text, out pool);

        if (pool == null)
        {
          pool = new MySqlPool(settings);
          Pools.Add(text, pool);
        }
        else
          pool.Settings = settings;

        return pool;
      }
    }

    public static void RemoveConnection(Driver driver)
    {
      Debug.Assert(driver != null);

      MySqlPool pool = driver.Pool;

      pool?.RemoveConnection(driver);
    }

    public static void ReleaseConnection(Driver driver)
    {
      Debug.Assert(driver != null);

      MySqlPool pool = driver.Pool;

      pool?.ReleaseConnection(driver);
    }

    public static void ClearPool(MySqlConnectionStringBuilder settings)
    {
      Debug.Assert(settings != null);
      string text;
      try
      {
        text = GetKey(settings);
      }
      catch (MySqlException)
      {
        // Cannot retrieve windows identity for IntegratedSecurity=true
        // This can be ignored.
        return;
      }
      ClearPoolByText(text);
    }

    private static void ClearPoolByText(string key)
    {
      lock (Pools)
      {
        // if pools doesn't have it, then this pool must already have been cleared
        if (!Pools.ContainsKey(key)) return;

        // add the pool to our list of pools being cleared
        MySqlPool pool = (Pools[key] as MySqlPool);
        ClearingPools.Add(pool);

        // now tell the pool to clear itself
        pool.Clear();

        // and then remove the pool from the active pools list
        Pools.Remove(key);
      }
    }

    public static void ClearAllPools()
    {
      lock (Pools)
      {
        // Create separate keys list.
        List<string> keys = new List<string>(Pools.Count);
        keys.AddRange(Pools.Keys);

        // Remove all pools by key.
        foreach (string key in keys)
          ClearPoolByText(key);
      }
    }

    public static void RemoveClearedPool(MySqlPool pool)
    {
      Debug.Assert(ClearingPools.Contains(pool));
      ClearingPools.Remove(pool);
    }

    /// <summary>
    /// Remove drivers that have been idle for too long.
    /// </summary>
    public static void CleanIdleConnections(object obj)
    {
      List<Driver> oldDrivers = new List<Driver>();
      lock (Pools)
      {
        foreach (MySqlPool pool in Pools.Keys.Select(key => Pools[key]))
        {
          oldDrivers.AddRange(pool.RemoveOldIdleConnections());
        }
      }
      foreach (Driver driver in oldDrivers)
      {
        driver.Close();
      }
    }

    /// <summary>
    /// Remove hosts from the demoted list that have already been there for more
    /// than 120,000 milliseconds and add them to the available hosts list.
    /// </summary>
    internal static void ReleaseDemotedHosts(object state)
    {
      if (_demotedHosts.TryPeek(out FailoverServer demotedServer))
      {
        if (demotedServer.DemotedTime.AddMilliseconds(_demotedTimeout) < DateTime.Now)
        {
          demotedServer.Attempted = false;
          _hosts.Add(demotedServer);
          _demotedHosts.TryDequeue(out demotedServer);
        }
      }

      _demotedServersTimer.Change(_demotedTimeout, Timeout.Infinite);
    }
  }
}