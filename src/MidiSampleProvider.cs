using MeltySynth;
using NAudio.Wave;
using System;

namespace DescentView
{
    public class MidiSampleProvider : ISampleProvider
	{
		private readonly Synthesizer _synthesizer;
		private readonly MidiFileSequencer _sequencer;
		private readonly int _sampleRate;
		private readonly TimeSpan _totalDuration;
		private long _samplesRendered;
		private bool _isPlaying;
		private MidiFile? _currentMidiFile;

		public MidiSampleProvider(Synthesizer synthesizer, MidiFileSequencer sequencer, MidiFile midiFile)
		{
			_synthesizer = synthesizer;
			_sequencer = sequencer;
			_currentMidiFile = midiFile;
			_sampleRate = synthesizer.SampleRate;
			_totalDuration = midiFile.Length;
			_samplesRendered = 0;
			_isPlaying = false;
			WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
		}

		public WaveFormat WaveFormat { get; }

		public TimeSpan CurrentTime => TimeSpan.FromSeconds((double)_samplesRendered / _sampleRate);
		public TimeSpan TotalDuration => _totalDuration;
		public bool IsPlaying => _isPlaying;

		public void Play(bool loop)
		{
			if (_currentMidiFile != null)
			{
				_sequencer.Play(_currentMidiFile, loop);
				_isPlaying = true;
				_samplesRendered = 0;
			}
		}

		public void Stop()
		{
			_sequencer.Stop();
			_isPlaying = false;
			_samplesRendered = 0;
		}

		public int Read(float[] buffer, int offset, int count)
		{
			// MeltySynth renders to separate left/right buffers
			var sampleCount = count / 2;
			var left = new float[sampleCount];
			var right = new float[sampleCount];

			_sequencer.Render(left, right);

			// Interleave left and right channels
			for (int i = 0; i < sampleCount; i++)
			{
				buffer[offset + i * 2] = left[i];
				buffer[offset + i * 2 + 1] = right[i];
			}

			_samplesRendered += sampleCount;

			if (CurrentTime >= _totalDuration)
			{
				_isPlaying = false;
			}

			return count;
		}

		public void Seek(TimeSpan position)
		{
			// MeltySynth doesn't have a seek method - restart and discard samples until we reach the correct position
			if (_currentMidiFile != null)
			{
				_sequencer.Play(_currentMidiFile, true);
				_samplesRendered = (long)(position.TotalSeconds * _sampleRate);
			}
		}
	}
}