﻿/*
 * (C) Copyright 2014 Amaury Crickx
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
 *
 */

using Recognito.Distances;
using Recognito.Enchancements;
using Recognito.Features;
using Recognito.Vad;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Recognito
{
    public class Recognito<T>
    {
        private static readonly float MIN_SAMPLE_RATE = 8000.0f;
        private readonly object _lock = new object();

        private readonly Dictionary<T, VoicePrint> store = new Dictionary<T, VoicePrint>();
        private readonly float sampleRate;

        private volatile bool universalModelWasSetByUser = new bool();
        private VoicePrint universalModel;


        /**
         * Default constructor
         * @param sampleRate the sample rate, at least 8000.0 Hz (preferably higher)
         */
        public Recognito(float sampleRate)
        {
            if (sampleRate < MIN_SAMPLE_RATE)
            {
                throw new ArgumentException("Sample rate should be at least 8000 Hz");
            }
            this.sampleRate = sampleRate;
        }

        /**
         * Constructor taking previously extracted voice prints directly into the system
         * @param sampleRate the sample rate, at least 8000.0 Hz (preferably higher)
         * @param voicePrintsByUserKey a {@code Map} containing user keys and their respective {@code VoicePrint}
         */
        public Recognito(float sampleRate, Dictionary<T, VoicePrint> voicePrintsByUserKey)
            : this(sampleRate)
        {


            var it = voicePrintsByUserKey.GetEnumerator();
            if (it.MoveNext())
            {
                var print = it.Current.Value;
                universalModel = new VoicePrint(print);

                while (it.MoveNext())
                {
                    universalModel.Merge(it.Current.Value);
                }

            }

            store.AddRange(voicePrintsByUserKey);
        }

        /**
         * Get the universal model
         * @return the universal model
         */
        public VoicePrint getUniversalModel()
        {
            return new VoicePrint(universalModel);
        }

        /**
         * Sets the universal model to be used to calculate likelihood ratios
         * Once set, further voice print create / merge operations won't modify this model
         * @param universalModel the universal model to set, may not be null
         */
        public void SetUniversalModel(VoicePrint universalModel)
        {
            lock (_lock)
            {
                if (universalModel == null)
                {
                    throw new ArgumentNullException(nameof(universalModel), "The universal model may not be null");
                }

                universalModelWasSetByUser = false;

                this.universalModel = universalModel;
            }
        }

        /**
         * Creates a voice print and stores it along with the user key for later comparison with new samples
         * <p>
         * Threading : this method is synchronized to prevent inadvertently erasing an existing user key
         * </p>
         * @param userKey the user key associated with this voice print
         * @param voiceSample the voice sample, values between -1.0 and 1.0
         * @return the voice print extracted from the given sample
         */
        public VoicePrint CreateVoicePrint(T userKey, double[] voiceSample)
        {
            lock (_lock)
            {
                if (userKey == null)
                {
                    throw new ArgumentNullException(nameof(userKey), "The userKey is null");
                }

                if (store.ContainsKey(userKey))
                {
                    throw new ArgumentException("The userKey already exists: [{userKey}");
                }

                double[] features = ExtractFeatures(voiceSample, sampleRate);
                VoicePrint voicePrint = new VoicePrint(features);

                if (!universalModelWasSetByUser)
                {
                    if (universalModel == null)
                    {
                        universalModel = new VoicePrint(voicePrint);
                    }
                    else
                    {
                        universalModel.Merge(features);
                    }
                }

                store.Add(userKey, voicePrint);

                return voicePrint;
            }
        }

        /**
         * Convenience method to load voice samples from files.
         * <p>
         * See class description for details on files
         * </p>
         * @param userKey the user key associated with this voice print
         * @param voiceSampleFile the file containing the voice sample, must have the same sample rate as defined in constructor
         * @return the voice print
         * @throws UnsupportedAudioFileException when the JVM does not support the file format
         * @throws IOException when an I/O exception occurs
         * @see Recognito#createVoicePrint(Object, double[], float)
         */
        public VoicePrint CreateVoicePrint(T userKey, FileStream voiceSampleFile)
        {
            var audioSample = ConvertFileToDoubleArray(voiceSampleFile);

            return CreateVoicePrint(userKey, audioSample);
        }

        /**
         * Converts the given audio file to an array of doubles with values between -1.0 and 1.0
         * @param voiceSampleFile the file to convert
         * @return an array of doubles
         * @throws UnsupportedAudioFileException when the JVM does not support the file format
         * @throws IOException when an I/O exception occurs
         */
        private double[] ConvertFileToDoubleArray(FileStream voiceSampleFile)
        {

            //AudioInputStream sample = AudioSystem.getAudioInputStream(voiceSampleFile);
            //AudioFormat format = sample.getFormat();
            //float diff = Math.Abs(format.getSampleRate() - sampleRate);
            //if (diff > 5 * MathHelper.Ulp(0.0f))
            //{
            //    throw new IllegalArgumentException("The sample rate for this file is different than Recognito's " +
            //            "defined sample rate : [" + format.getSampleRate() + "]");
            //}
            //return FileHelper.readAudioInputStream(sample);

            return null;
        }

        /**
         * Extracts voice features from the given voice sample and merges them with previous voice 
         * print extracted for this user key
         * <p>
         * Threading : it is safe to simultaneously add voice samples for a single userKey from multiple threads
         * </p>
         * @param userKey the user key associated with this voice print
         * @param voiceSample the voice sample to analyze, values between -1.0 and 1.0
         * @return the updated voice print
         */
        public VoicePrint MergeVoiceSample(T userKey, double[] voiceSample)
        {

            lock (_lock)
            {

                if (userKey == null)
                {
                    throw new ArgumentNullException(nameof(userKey), "The userKey is null");
                }

                store.TryGetValue(userKey, out VoicePrint original);
                if (original == null)
                {
                    throw new ArgumentException($"No voice print linked to this user key [{userKey}");
                }


                double[] features = ExtractFeatures(voiceSample, sampleRate);

                if (!universalModelWasSetByUser)
                {
                    universalModel.Merge(features);
                }

                original.Merge(features);

                return original;
            }
        }

        /**
         * Convenience method to merge voice samples from files. 
         * <p>
         * See class description for details on files
         * </p>
         * @param userKey the user key associated with this voice print
         * @param voiceSampleFile the file containing the voice sample, must have the same sample rate as defined in constructor
         * @return the updated voice print
         * @throws UnsupportedAudioFileException when the JVM does not support the file format
         * @throws IOException when an I/O exception occurs
         * @see Recognito#mergeVoiceSample(Object, double[], float)
         */
        public VoicePrint MergeVoiceSample(T userKey, FileStream voiceSampleFile)
        {
            double[] audioSample = ConvertFileToDoubleArray(voiceSampleFile);

            return MergeVoiceSample(userKey, audioSample);
        }

        /**
         * Calculates the distance between this voice sample and the voice prints previously extracted 
         * and returns the closest matches sorted by distance
         * <p>
         * Usage of a closed set is assumed : the speaker's voice print was extracted before and is known to the system.
         * This means you'll always get MatchResults even if the speaker is absolutely unknown to the system.
         * The MatchResult class provides a likelihood ratio in order to help determining the usefulness of the result
         * </p>
         * @param voiceSample the voice sample, values between -1.0 and 1.0
         * @return a list MatchResults sorted by distance
         */
        public IEnumerable<MatchResult<T>> Identify(double[] voiceSample)
        {

            if (store.Count == 0)
            {
                throw new InvalidOperationException("There is no voice print enrolled in the system yet");
            }

            VoicePrint voicePrint = new VoicePrint(ExtractFeatures(voiceSample, sampleRate));

            var calculator = new EuclideanDistanceCalculator();
            var matches = new List<MatchResult<T>>(store.Count);

            var distanceFromUniversalModel = voicePrint.GetDistance(calculator, universalModel);

            foreach (var entry in store)
            {
                var distance = entry.Value.GetDistance(calculator, voicePrint);
                // likelihood : how close is the given voice sample to the current VoicePrint 
                // compared to the total distance between the current VoicePrint and the universal model 
                int likelihood = 100 - (int)(distance / (distance + distanceFromUniversalModel) * 100);
                matches.Add(new MatchResult<T>(entry.Key, likelihood, distance));
            }

            matches = matches.OrderBy(m => m.Distance).ToList();

            return matches;
        }

        /**
         * Convenience method to identify voice samples from files.
         * <p>
         * See class description for details on files
         * </p>
         * @param voiceSampleFile the file containing the voice sample
         * @return a list MatchResults sorted by distance
         * @throws UnsupportedAudioFileException when the JVM does not support the audio file format
         * @throws IOException when an I/O exception occurs
         * @see Recognito#identify(double[], float)
         */
        public IEnumerable<MatchResult<T>> Identify(FileStream voiceSampleFile)
        {
            var audioSample = ConvertFileToDoubleArray(voiceSampleFile);

            return Identify(audioSample);
        }

        /**
         * Removes silence, applies normalization and extracts voice features from the given sample
         * @param voiceSample the voice sample
         * @param sampleRate the sample rate
         * @return the extracted features
         */
        private double[] ExtractFeatures(double[] voiceSample, float sampleRate)
        {
            var voiceDetector = new AutocorrellatedVoiceActivityDetector();
            var normalizer = new Normalizer();

            var lpcExtractor = new LpcFeaturesExtractor(sampleRate, 20);


            voiceDetector.RemoveSilence(voiceSample, sampleRate);
            normalizer.normalize(voiceSample, sampleRate);
            double[] lpcFeatures = lpcExtractor.ExtractFeatures(voiceSample);

            return lpcFeatures;
        }

    }
}