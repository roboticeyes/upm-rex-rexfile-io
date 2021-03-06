﻿/*
 * Copyright (c) 2019 Robotic Eyes GmbH software
 *
 * THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
 * KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
 * PARTICULAR PURPOSE.
 *
 */

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
using ColoredPoint = RoboticEyes.Rex.RexFileReader.Examples.ThreadedClustering.ColoredPoint;
using Debug = UnityEngine.Debug;

namespace RoboticEyes.Rex.RexFileReader.Examples
{
    public class PointListClusteredParticlesPrefab : RexPointListObject
    {
        [SerializeField]
        [Tooltip("LOD-like behaviour for large particle counts. Allows for more details close-up while maintaining overall performance")]
        private bool dynamicParticleDensityEnabled;
        [SerializeField]
        [Range(0.01f, 0.25f)]
        [Tooltip("Minimum change in density factor to update point clouds. (density factor = max allowed particles on screen divided by max available particles for visible clusters)")]
        private float minDeltaForDensityUpdate = 0.05f;
        [SerializeField]
        [Tooltip("Sets upper limit to avoid performance issues with large particle counts")]
        private int maxAllowedParticlesOnScreen = (int)10e5;
        [SerializeField]
        [Tooltip("Used to get number of clusters by dividing maxAllowedParticles. Default value should be okay for most uses.")]
        private int averageParticlesPerCluster = (int)60e3;
        [SerializeField]
        private PointListParticlesPrefab pointListParticlesPrefab;
        
        private int clusterCount;
        private float baseReductionFactor;
        private float minParticleSize;
        private PointListParticlesPrefab[] childPointListParticles;

        public override bool SetPoints (List<Vector3> pointPositions, List<Color> pointColors)
        {
            HandleStartup (ref pointPositions);

            var startingCentroids = GetRandomStartingCentroids (pointPositions, clusterCount);
            var coloredPoints = CombinePointInfo (pointPositions, pointColors);

            StartClusteringCoroutine (coloredPoints, startingCentroids, (success, clusteredPointPositions) =>
                {
                    if (!success)
                    {
                        return;
                    }
                    
                    StartCoroutine (ParticleSystemSpawnerCoroutine (clusteredPointPositions));
                }
            );

            return true;
        }
        
        private void HandleStartup (ref List<Vector3> pointPositions)
        {
            baseReductionFactor = Mathf.Min (1, (float)maxAllowedParticlesOnScreen / pointPositions.Count);
            clusterCount = Mathf.CeilToInt ((float)pointPositions.Count / averageParticlesPerCluster);

            if (maxAllowedParticlesOnScreen >= pointPositions.Count)
            {
                dynamicParticleDensityEnabled = false;
            }

            if (pointPositions.Count > maxAllowedParticlesOnScreen)
            {
                print ($"point cloud of {pointPositions.Count} reduced to {maxAllowedParticlesOnScreen} particles");
            }
        }

        private IEnumerator ParticleSystemSpawnerCoroutine (IReadOnlyList<List<ColoredPoint>> clusteredPointPositions)
        {
            childPointListParticles = new PointListParticlesPrefab[clusteredPointPositions.Count];

            for (var i = 0; i < clusteredPointPositions.Count; i++)
            {
                var child = Instantiate (pointListParticlesPrefab, transform);

                child.SetPoints (clusteredPointPositions[i]);
                child.SetMinDeltaForDensityUpdate (minDeltaForDensityUpdate);
                child.SetDensity (baseReductionFactor);

                childPointListParticles[i] = child;
                
                yield return null;
            }
        }

        private void StartClusteringCoroutine (IEnumerable<ColoredPoint> coloredPoints, Vector3[] startingCentroids, ClusterResultDelegate resultDelegate)
        {
            StartCoroutine (ClusterPointsCoroutine (coloredPoints, startingCentroids, clusterCount, resultDelegate));
        }

        private delegate void ClusterResultDelegate (bool success, List<ColoredPoint>[] loadedObjects);

        private static IEnumerator ClusterPointsCoroutine (IEnumerable<ColoredPoint> pointPositions, Vector3[] startingCentroids, int kVal, ClusterResultDelegate result)
        {
            var stopwatch = new Stopwatch ();
            stopwatch.Start ();
            Debug.Log ("starting clustering...");

            var threadedClustering = new ThreadedClustering (pointPositions, startingCentroids, kVal);
            threadedClustering.StartJob ();

            var waiter = new WaitForSeconds (0.05f);

            while (!threadedClustering.IsDone)
            {
                yield return waiter;
            }

            stopwatch.Stop ();
            Debug.Log  ($"clustering took {stopwatch.Elapsed.Milliseconds} milliseconds");

            result (threadedClustering.clusteredPoints != null, threadedClustering.clusteredPoints);
        }


        private static IEnumerable<ColoredPoint> CombinePointInfo (IReadOnlyList<Vector3> pointPositions, IEnumerable<Color> pointColors)
        {
            return pointColors.Select ((color, i) => new ColoredPoint {point = pointPositions[i], color = color});
        }


        private static Vector3[] GetRandomStartingCentroids (IReadOnlyList<Vector3> pointPositions, int kVal)
        {
            var centroids = new Vector3[kVal];

            for (var i = 0; i < kVal; i++)
            {
                centroids[i] = pointPositions[Random.Range (0, pointPositions.Count)];
            }

            return centroids;
        }

        public override void SetRendererEnabled (bool enabled)
        {
            foreach (var plpp in childPointListParticles)
            {
                plpp.SetRendererEnabled (enabled);
            }
        }

        public override void SetLayer (int layer)
        {
            foreach (var plpp in childPointListParticles)
            {
                plpp.SetLayer (layer);
            }
        }

        private void Update()
        {
            if (!dynamicParticleDensityEnabled)
            {
                return;
            }

            //skip if none or not all clusters are done instantiating
            if (childPointListParticles?.Last() == null)
            {
                return;
            }

            var visibleChildPointListParticles = childPointListParticles.Where (x => x.IsVisibleByCamera ());
            var maxTotalVisibleParticles = visibleChildPointListParticles.Sum (x => x.GetParticleCount ());
            var reductionFactor = Mathf.Min ((float)maxAllowedParticlesOnScreen / maxTotalVisibleParticles, 1);

            var sw = new Stopwatch ();
            sw.Start ();
            foreach (var childPointListParticle in visibleChildPointListParticles)
            {
                if (childPointListParticle.IsVisibleByCamera ())
                {
                    childPointListParticle.SetDensity (reductionFactor);
                }

                if (sw.ElapsedMilliseconds > 10)
                {
                    break;
                }
            }

            sw.Restart ();
            foreach (var childPointListParticle in childPointListParticles.Where (x => !x.IsVisibleByCamera ()))
            {
                childPointListParticle.SetDensity (0.1f);
                if (sw.ElapsedMilliseconds > 10)
                {
                    break;
                }
            }
        }
    }
}