﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace fanuc.veneers
{
    public class Veneers
    {
        Action<Veneer> change_print = (v) =>
        {
            Console.WriteLine(DateTime.UtcNow + "::delta=" + v.ChangeDelta + "::method=" + v.LastInput.method + "::note=" + v.Note + "::marker=" + v.Marker + "::data=" + v.DataDelta);
        };

        Action<Veneer> error_print = (v) =>
        {
            Console.WriteLine(JObject.FromObject(v.LastInput).ToString());
        };

        private List<Veneer> _wholeVeneers = new List<Veneer>();
        private Dictionary<dynamic, List<Veneer>> _slicedVeneers = new Dictionary<dynamic, List<Veneer>>();
        public void Slice(dynamic split)
        {
            foreach (var key in split)
            {
                _slicedVeneers[key] = new List<Veneer>();
            }
        }

        public void Add(Type veneerType, string note)
        {
            Veneer veneer = (Veneer)Activator.CreateInstance(veneerType, new object[] {note});
            veneer.OnChange = (v) => change_print(v);
            veneer.OnError = (v) => error_print(v);
            _wholeVeneers.Add(veneer);
        }

        public void AddAcrossSlices(Type veneerType, string note)
        {
            foreach (var key in _slicedVeneers.Keys)
            {
                Veneer veneer = (Veneer)Activator.CreateInstance(veneerType, new object[] {note});
                veneer.OnChange = (v) => change_print(v);
                veneer.OnError = (v) => error_print(v);
                _slicedVeneers[key].Add(veneer);
            }
        }

        public dynamic Peel(string note, dynamic input)
        {
            return _wholeVeneers.FirstOrDefault(v => v.Note == note).Peel(input);
        }
        
        public dynamic PeelAcross(dynamic split, string note, dynamic input)
        {
            foreach (var key in _slicedVeneers.Keys)
            {
                if (key.Equals(split))
                {
                    foreach (Veneer veneer in _slicedVeneers[key])
                    {
                        if (veneer.Note == note)
                        {
                            return veneer.Peel(input);
                        }
                    }
                }
            }

            return new { };
        }

        public void Mark(dynamic split, dynamic marker)
        {
            foreach (var key in _slicedVeneers.Keys)
            {
                if (key.Equals(split))
                {
                    foreach (Veneer veneer in _slicedVeneers[key])
                    {
                        veneer.Mark(marker);
                    }
                }
            }
        }
    }
    
    public class Veneer
    {
        public TimeSpan ChangeDelta
        {
            get { return _stopwatch.Elapsed; }
        }
        
        protected Stopwatch _stopwatch = new Stopwatch();

        public string Note
        {
            get { return _note; }
        }
        
        protected string _note = "";
        
        public dynamic Marker
        {
            get { return _marker; }
        }
        
        protected dynamic _marker = new { };
        
        protected bool _hasMarker = false;
        
        public dynamic LastInput
        {
            get { return _lastInput; }
        }
        
        protected dynamic _lastInput = new { };

        public dynamic DataDelta
        {
            get { return _lastValue; }
        }
        
        protected dynamic _lastValue = new { };

        protected bool _isFirstCall = true;
        
        public Action<Veneer> OnError = (veneer) => { };

        public Action<Veneer> OnChange =  (veneer) => { };

        public Veneer(string note = "")
        {
            _note = note;
            _stopwatch.Start();
        }

        protected void dataChanged(dynamic input, dynamic current_value)
        {
            this._lastInput = input;
            this._lastValue = current_value;
            this.OnChange(this);
            _stopwatch.Restart();
        }
        
        public void Mark(dynamic marker)
        {
            _marker = marker;
            _hasMarker = true;
        }
        
        protected virtual dynamic First(dynamic input)
        {
            return new { };
        }

        protected virtual dynamic Next(dynamic input)
        {
            return new { };
        }

        protected virtual dynamic Any(dynamic input)
        {
            return new { };
        }

        public dynamic Peel(dynamic input)
        {
            if(_isFirstCall)
            {
                _isFirstCall = false;
                return this.First(input);
            }
            else
            {
                return this.Any(input);
            }
        }
    }

    public class RdParamLData : Veneer
    {
        public RdParamLData(string note = "") : base(note)
        {
            _lastValue = -1;
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                int pc = input.response.cnc_rdparam.param.ldata;
                if (pc != this._lastValue)
                {
                    this.dataChanged(input, pc);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }
    
    public class RdDynamic2 : Veneer
    {
        public RdDynamic2(string note = "") : base(note)
        {
            _lastValue = new
            {
                actual_feedrate = -1,
                actual_spindle_speed = -1,
                alarm = -1,
                main_program = -1, 
                current_program = -1, 
                sequence_number = -1
            };
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                dynamic d = input.response.cnc_rddynamic2.rddynamic;
                
                dynamic current_value = new
                {
                    actual_feedrate = d.actf,
                    actual_spindle_speed = d.acts,
                    alarm = d.alarm,
                    main_program = d.prgmnum,
                    current_program = d.prgnum,
                    sequence_number = d.seqnum
                };
                
                if (!current_value.Equals(this._lastValue))
                {
                    this.dataChanged(input, current_value);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }
    
    public class Block : Veneer
    {
        public Block(string note = "") : base(note)
        {
            _lastValue = string.Empty;
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                string source = string.Join("", input.response.cnc_rdexecprog.data).Trim();
                string[] source_lines = source.Split('\n');
                string source_line = source_lines[0].Trim(char.MinValue, ' ');
                if (source_line != this._lastValue)
                {
                    this.dataChanged(input, source_line);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }

    public class GetPath : Veneer
    {
        public GetPath(string note = "") : base(note)
        {
            _lastValue = new
            {
                path_no = -1,
                maxpath_no = -1
            };
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                var current_value = new
                {
                    path_no = input.response.cnc_getpath.path_no,
                    maxpath_no = input.response.cnc_getpath.maxpath_no
                };
                
                if (!current_value.Equals(this._lastValue))
                {
                    this.dataChanged(input, current_value);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }
    
    public class StatInfo : Veneer
    {
        public StatInfo(string note = "") : base(note)
        {
            _lastValue = new
            {
                aut = -1,
                run = -1,
                edit = -1,
                motion = -1,
                mstb = -1,
                emergency = -1,
                alarm = -1
            };
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                var current_value = new
                {
                    aut = input.response.cnc_statinfo.statinfo.aut,
                    run = input.response.cnc_statinfo.statinfo.run,
                    edit = input.response.cnc_statinfo.statinfo.edit,
                    motion = input.response.cnc_statinfo.statinfo.motion,
                    mstb = input.response.cnc_statinfo.statinfo.mstb,
                    emergency = input.response.cnc_statinfo.statinfo.emergency,
                    alarm = input.response.cnc_statinfo.statinfo.alarm
                };
                
                if (!current_value.Equals(this._lastValue))
                {
                    this.dataChanged(input, current_value);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }
    
    public class SysInfo : Veneer
    {
        public SysInfo(string note = "") : base(note)
        {
            _lastValue = new
            {
                max_axis = -1,
                cnc_type = string.Empty,
                mt_type = string.Empty,
                series = string.Empty,
                version = string.Empty,
                axes = string.Empty
            };
        }
        
        protected override dynamic Any(dynamic input)
        {
            if (input.success)
            {
                var current_value = new
                {
                    max_axis = input.response.cnc_sysinfo.sysinfo.max_axis,
                    cnc_type = string.Join("", input.response.cnc_sysinfo.sysinfo.cnc_type),
                    mt_type = string.Join("", input.response.cnc_sysinfo.sysinfo.mt_type),
                    series = string.Join("", input.response.cnc_sysinfo.sysinfo.series),
                    version = string.Join("", input.response.cnc_sysinfo.sysinfo.version),
                    axes = string.Join("", input.response.cnc_sysinfo.sysinfo.axes)
                };
                
                if (!current_value.Equals(this._lastValue))
                {
                    this.dataChanged(input, current_value);
                }
            }
            else
            {
                this.OnError(input);
            }

            return new { veneer = this };
        }
    }
    
    public class Connect : Veneer
    {
        public Connect(string note = "") : base(note)
        {

        }

        protected override dynamic First(dynamic input)
        {
            this.dataChanged(input, input.success);
            
            return new { veneer = this };
        }

        protected override dynamic Any(dynamic input)
        {
            if (_lastValue != input.success)
            {
                this.dataChanged(input, input.success);
            }
            
            return new { veneer = this };
        }
    }
}