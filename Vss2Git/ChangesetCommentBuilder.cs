/* Copyright 2016 ELCOM, spoločnosť s ručením obmedzeným, Prešov
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */


using System.Collections.Generic;
using System.Text;
using Hpdi.VssLogicalLib;

namespace Hpdi.Vss2Git
{
  internal class ChangesetCommentBuilder
  {
    private Dictionary<VssActionType, int> operations;
    private int count = 0;

    public ChangesetCommentBuilder()
    {
      this.operations = new Dictionary<VssActionType, int>(14);
      this.count = 0;
    }

    public void Add(VssActionType action)
    {
      if (this.operations.ContainsKey(action))
        this.operations[action]++;
      else
        this.operations.Add(action, 1);
      this.count++;
    }

    public override string ToString()
    {
      if (this.operations.Count < 1)
        return string.Empty;
      StringBuilder sb = new StringBuilder(1024);
      foreach (KeyValuePair<VssActionType, int> val in this.operations)
      {
        if (val.Value == 1)
          sb.Append(val.Key);
        else if (val.Value != 1)
          sb.Append(val.Key).Append('[').Append(val.Value).Append(']');
        sb.Append(", ");
      }
      if (sb.Length > 2)
        sb.Length -= 2;
      return sb.ToString();
    }

    public int Count { get { return this.count; } }

    public static string GetComment(Changeset changeset)
    {
      if (object.ReferenceEquals(changeset, null))
        return null;
      ChangesetCommentBuilder cb = new ChangesetCommentBuilder();
      foreach (Revision revision in changeset.Revisions)
      {
        if (revision.Item.IsProject)
          cb.Add(revision.Action.Type);
      }
      if (cb.Count > 0)
        return cb.ToString();
      else
        return null;
    }
  }
}
