﻿//#define SMR_BAKEMESH_SKIPCALCBOUNDS

using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;

namespace Unity.DemoTeam.DigitalHuman
{
	[ExecuteAlways]
	public class SkinAttachmentTarget : MonoBehaviour
	{
		public struct MeshInfo
		{
			public MeshBuffers meshBuffers;
			public MeshAdjacency meshAdjacency;
			public KdTree3 meshVertexBSP;
			public bool valid;
		}

		[HideInInspector] public List<SkinAttachment> subjects = new List<SkinAttachment>();
		[HideInInspector] private bool subjectsChanged = false;

		[NonSerialized] public Mesh meshBakedSmr;
		[NonSerialized] public Mesh meshBakedOrAsset;
		[NonSerialized] public MeshBuffers meshBuffers;
		[NonSerialized] public Mesh meshBuffersLastAsset;

		public SkinAttachmentData attachData;

		[Header("Debug options")]
		public bool showWireframe = false;
		public bool showUVSeams = false;
		public bool showResolved = false;
		public bool showMouseOver = false;

		private MeshInfo cachedMeshInfo;
		private int cachedMeshInfoFrame = -1;

		private JobHandle[] stagingJobs;
		private Vector3[][] stagingData;
		private GCHandle[] stagingPins;

		void OnEnable()
		{
			UpdateMeshBuffers();
		}

		void LateUpdate()
		{
			if (UpdateMeshBuffers())
			{
				if (subjectsChanged)
				{
					if (AttachSubjects())
					{
						subjectsChanged = false;
					}
				}
				else
				{
					ResolveSubjects();
				}
			}
		}

		bool UpdateMeshBuffers()
		{
			meshBakedOrAsset = null;
			{
				var mf = GetComponent<MeshFilter>();
				if (mf != null)
				{
					meshBakedOrAsset = mf.sharedMesh;
				}

				var smr = GetComponent<SkinnedMeshRenderer>();
				if (smr != null)
				{
					if (meshBakedSmr == null)
					{
						meshBakedSmr = new Mesh();
						meshBakedSmr.name = "SkinAttachmentTarget(BakeMesh)";
						meshBakedSmr.hideFlags = HideFlags.HideAndDontSave & ~HideFlags.DontUnloadUnusedAsset;
						meshBakedSmr.MarkDynamic();
					}

					meshBakedOrAsset = meshBakedSmr;

					Profiler.BeginSample("smr.BakeMesh");
					{
						smr.BakeMesh(meshBakedSmr);
						{
							meshBakedSmr.bounds = smr.bounds;
						}
					}
					Profiler.EndSample();
				}
			}

			if (meshBakedOrAsset == null)
				return false;

			if (meshBuffers == null || meshBuffersLastAsset != meshBakedOrAsset)
			{
				meshBuffers = new MeshBuffers(meshBakedOrAsset);
			}
			else
			{
				meshBuffers.LoadPositionsFrom(meshBakedOrAsset);
				meshBuffers.LoadNormalsFrom(meshBakedOrAsset);
			}

			meshBuffersLastAsset = meshBakedOrAsset;
			return true;
		}

		void UpdateMeshInfo(ref MeshInfo info)
		{
			Profiler.BeginSample("upd-mesh-inf");
			if (meshBuffers == null)
			{
				info.valid = false;
			}
			else
			{
				info.meshBuffers = meshBuffers;

				const bool weldedAdjacency = false;//TODO enable for more reliable poses along uv seams
				if (info.meshAdjacency == null)
					info.meshAdjacency = new MeshAdjacency(meshBuffers, weldedAdjacency);
				else if (info.meshAdjacency.vertexCount != meshBuffers.vertexCount)
					info.meshAdjacency.LoadFrom(meshBuffers, weldedAdjacency);

				if (info.meshVertexBSP == null)
					info.meshVertexBSP = new KdTree3(meshBuffers.vertexPositions, meshBuffers.vertexCount);
				else
					info.meshVertexBSP.BuildFrom(meshBuffers.vertexPositions, meshBuffers.vertexCount);

				info.valid = true;
			}
			Profiler.EndSample();
		}

		public ref MeshInfo GetCachedMeshInfo()
		{
			int frameIndex = Time.frameCount;
			if (frameIndex != cachedMeshInfoFrame)
			{
				UpdateMeshInfo(ref cachedMeshInfo);

				if (cachedMeshInfo.valid)
					cachedMeshInfoFrame = frameIndex;
			}
			return ref cachedMeshInfo;
		}

		public void Attach(SkinAttachment subject)
		{
			Debug.Assert(subject.targetActive == null);
			subject.targetActive = this;

			if (subjects.Contains(subject) == false)
			{
				subjects.Add(subject);

				switch (subject.attachmentMode)
				{
					case SkinAttachment.AttachmentMode.BuildPoses:
						subjectsChanged = true;
						break;

					case SkinAttachment.AttachmentMode.LinkPosesByReference:
						Debug.Assert(subject.attachmentLink != null);
						subject.attachmentType = subject.attachmentLink.attachmentType;
						subject.attachmentIndex = subject.attachmentLink.attachmentIndex;
						subject.attachmentCount = subject.attachmentLink.attachmentCount;
						break;

					case SkinAttachment.AttachmentMode.LinkPosesBySpecificIndex:
						subject.attachmentIndex = Mathf.Clamp(subject.attachmentIndex, 0, attachData.itemCount - 1);
						subject.attachmentCount = Mathf.Clamp(subject.attachmentCount, 0, attachData.itemCount - subject.attachmentIndex);
						break;
				}

				Debug.Assert(subject.target.attachData == attachData);

				subject.attachedLocalPosition = subject.transform.localPosition;
				subject.attachedLocalRotation = subject.transform.localRotation;
			}
		}

		public void Detach(SkinAttachment subject)
		{
			Debug.Assert(subject.targetActive == this);
			subject.targetActive = null;

			if (subjects.Contains(subject))
			{
				subjects.Remove(subject);

				if (subject.attachmentMode == SkinAttachment.AttachmentMode.BuildPoses)
					subjectsChanged = true;
			}

			if (subject.attachmentMode != SkinAttachment.AttachmentMode.LinkPosesBySpecificIndex)
			{
				subject.attachmentIndex = -1;
				subject.attachmentCount = 0;
			}

			if (subject.preserveResolved == false)
			{
				subject.transform.localPosition = subject.attachedLocalPosition;
				subject.transform.localRotation = subject.attachedLocalRotation;
			}
		}

		bool AttachSubjects()
		{
			if (attachData == null)
				return false;

			var meshInfo = GetCachedMeshInfo();
			if (meshInfo.valid == false)
				return false;

			attachData.Clear();
			{
				subjects.RemoveAll(p => (p == null));
				for (int i = 0, n = subjects.Count; i != n; i++)
				{
					AttachSubject(ref meshInfo, subjects[i]);
				}
			}

			attachData.Persist();
			return true;
		}

		void AttachSubject(ref MeshInfo meshInfo, SkinAttachment subject)
		{
			Profiler.BeginSample("attach-subj");

			var subjectToTarget = this.transform.worldToLocalMatrix * subject.transform.localToWorldMatrix;

			switch (subject.attachmentType)
			{
				case SkinAttachment.AttachmentType.Transform:
					unsafe
					{
						var targetPosition = subjectToTarget.MultiplyPoint3x4(Vector3.zero);
						var targetNormal = subjectToTarget.MultiplyVector(Vector3.up);

						fixed (int* attachmentIndex = &subject.attachmentIndex)
						fixed (int* attachmentCount = &subject.attachmentCount)
						{
							AttachToClosestVertex(ref meshInfo, &targetPosition, &targetNormal, 1, attachmentIndex, attachmentCount);
						}
					}
					break;
				case SkinAttachment.AttachmentType.Mesh:
					unsafe
					{
						if (subject.meshInstance == null)
							break;

						var subjectVertexCount = subject.meshBuffers.vertexCount;
						var subjectPositions = subject.meshBuffers.vertexPositions;
						var subjectNormals = subject.meshBuffers.vertexNormals;

						using (var targetPositions = new UnsafeArrayVector3(subjectVertexCount))
						using (var targetNormals = new UnsafeArrayVector3(subjectVertexCount))
						{
							for (int i = 0; i != subjectVertexCount; i++)
							{
								targetPositions.val[i] = subjectToTarget.MultiplyPoint3x4(subjectPositions[i]);
								targetNormals.val[i] = subjectToTarget.MultiplyVector(subjectNormals[i]);
							}

							fixed (int* attachmentIndex = &subject.attachmentIndex)
							fixed (int* attachmentCount = &subject.attachmentCount)
							{
								AttachToClosestVertex(ref meshInfo, targetPositions.val, targetNormals.val, subjectVertexCount, attachmentIndex, attachmentCount);
							}
						}
					}
					break;
				case SkinAttachment.AttachmentType.MeshRoots:
					unsafe
					{
						if (subject.meshInstance == null)
							break;

						var subjectVertexCount = subject.meshBuffers.vertexCount;
						var subjectPositions = subject.meshBuffers.vertexPositions;
						var subjectNormals = subject.meshBuffers.vertexPositions;

						using (var targetPositions = new UnsafeArrayVector3(subjectVertexCount))
						using (var targetNormals = new UnsafeArrayVector3(subjectVertexCount))
						using (var targetOffsets = new UnsafeArrayVector3(subjectVertexCount))
						using (var targetVertices = new UnsafeArrayInt(subjectVertexCount))
						using (var rootIdx = new UnsafeArrayInt(subjectVertexCount))
						using (var rootDir = new UnsafeArrayVector3(subjectVertexCount))
						using (var rootGen = new UnsafeArrayInt(subjectVertexCount))
						using (var visitor = new UnsafeBFS(subjectVertexCount))
						{
							for (int i = 0; i != subjectVertexCount; i++)
							{
								targetPositions.val[i] = subjectToTarget.MultiplyPoint3x4(subjectPositions[i]);
								targetNormals.val[i] = subjectToTarget.MultiplyVector(subjectNormals[i]);
								targetOffsets.val[i] = Vector3.zero;
							}

							visitor.Clear();

							// find island roots
							for (int island = 0; island != subject.meshIslands.islandCount; island++)
							{
								int rootCount = 0;

								var bestDist0 = float.PositiveInfinity;
								var bestNode0 = -1;
								var bestVert0 = -1;

								var bestDist1 = float.PositiveInfinity;
								var bestNode1 = -1;
								var bestVert1 = -1;

								foreach (var i in subject.meshIslands.islandVertices[island])
								{
									var targetDist = float.PositiveInfinity;
									var targetNode = -1;

									if (meshInfo.meshVertexBSP.FindNearest(ref targetDist, ref targetNode, ref targetPositions.val[i]))
									{
										// found a root if one or more neighbouring vertices are below
										var bestDist = float.PositiveInfinity;
										var bestNode = -1;

										foreach (var j in subject.meshAdjacency.vertexVertices[i])
										{
											var targetDelta = targetPositions.val[j] - meshBuffers.vertexPositions[targetNode];
											var targetNormalDist = Vector3.Dot(targetDelta, meshBuffers.vertexNormals[targetNode]);
											if (targetNormalDist < 0.0f)
											{
												var d = Vector3.SqrMagnitude(targetDelta);
												if (d < bestDist)
												{
													bestDist = d;
													bestNode = j;
												}
											}
										}

										if (bestNode != -1)
										{
											visitor.Ignore(i);
											rootIdx.val[i] = targetNode;
											rootDir.val[i] = Vector3.Normalize(targetPositions.val[bestNode] - targetPositions.val[i]);
											rootGen.val[i] = 0;
											rootCount++;
										}
										else
										{
											rootIdx.val[i] = -1;
											rootGen.val[i] = -1;

											// see if node qualifies as second choice root
											var targetDelta = targetPositions.val[i] - meshBuffers.vertexPositions[targetNode];
											var targetNormalDist = Mathf.Abs(Vector3.Dot(targetDelta, meshBuffers.vertexNormals[targetNode]));
											if (targetNormalDist < bestDist0)
											{
												bestDist1 = bestDist0;
												bestNode1 = bestNode0;
												bestVert1 = bestVert0;

												bestDist0 = targetNormalDist;
												bestNode0 = targetNode;
												bestVert0 = i;
											}
											else if (targetNormalDist < bestDist1)
											{
												bestDist1 = targetNormalDist;
												bestNode1 = targetNode;
												bestVert1 = i;
											}
										}
									}
								}

								if (rootCount < 2 && bestVert0 != -1)
								{
									visitor.Ignore(bestVert0);
									rootIdx.val[bestVert0] = bestNode0;
									rootDir.val[bestVert0] = Vector3.Normalize(meshBuffers.vertexPositions[bestNode0] - targetPositions.val[bestVert0]);
									rootGen.val[bestVert0] = 0;
									rootCount++;

									if (rootCount < 2 && bestVert1 != -1)
									{
										visitor.Ignore(bestVert1);
										rootIdx.val[bestVert1] = bestNode1;
										rootDir.val[bestVert1] = Vector3.Normalize(meshBuffers.vertexPositions[bestNode1] - targetPositions.val[bestVert1]);
										rootGen.val[bestVert1] = 0;
										rootCount++;
									}
								}
							}

							// find boundaries
							for (int i = 0; i != subjectVertexCount; i++)
							{
								if (rootIdx.val[i] != -1)
									continue;

								foreach (var j in subject.meshAdjacency.vertexVertices[i])
								{
									if (rootIdx.val[j] != -1)
									{
										visitor.Insert(i);
										break;
									}
								}
							}

							// propagate roots
							while (visitor.MoveNext())
							{
								var i = visitor.position;

								var bestDist = float.PositiveInfinity;
								var bestNode = -1;

								foreach (var j in subject.meshAdjacency.vertexVertices[i])
								{
									if (rootIdx.val[j] != -1)
									{
										var d = -Vector3.Dot(rootDir.val[j], Vector3.Normalize(targetPositions.val[j] - targetPositions.val[i]));
										if (d < bestDist)
										{
											bestDist = d;
											bestNode = j;
										}
									}
									else
									{
										visitor.Insert(j);
									}
								}

								rootIdx.val[i] = rootIdx.val[bestNode];
								rootDir.val[i] = Vector3.Normalize(targetPositions.val[bestNode] - targetPositions.val[i]);
								rootGen.val[i] = rootGen.val[bestNode] + 1;

								targetOffsets.val[i] = targetPositions.val[i] - targetPositions.val[bestNode];
								targetPositions.val[i] = targetPositions.val[bestNode];
							}

							// copy to target vertices
							for (int i = 0; i != subjectVertexCount; i++)
							{
								targetVertices.val[i] = rootIdx.val[i];
							}

							fixed (int* attachmentIndex = &subject.attachmentIndex)
							fixed (int* attachmentCount = &subject.attachmentCount)
							{
								AttachToVertex(ref meshInfo, targetPositions.val, targetOffsets.val, targetNormals.val, targetVertices.val, subjectVertexCount, attachmentIndex, attachmentCount);
							}
						}
					}
					break;
			}

			Profiler.EndSample();
		}

		//--------
		// Attach

		public static unsafe int BuildPosesTriangle(ref MeshInfo meshInfo, SkinAttachmentPose* pose, ref Vector3 target, int triangle)
		{
			var meshPositions = meshInfo.meshBuffers.vertexPositions;
			var meshTriangles = meshInfo.meshBuffers.triangles;

			int _0 = triangle * 3;
			var v0 = meshTriangles[_0];
			var v1 = meshTriangles[_0 + 1];
			var v2 = meshTriangles[_0 + 2];

			var p0 = meshPositions[v0];
			var p1 = meshPositions[v1];
			var p2 = meshPositions[v2];

			var v0v1 = p1 - p0;
			var v0v2 = p2 - p0;

			var triangleNormal = Vector3.Cross(v0v1, v0v2);
			var triangleArea = Vector3.Magnitude(triangleNormal);

			triangleNormal /= triangleArea;
			triangleArea *= 0.5f;

			if (triangleArea < float.Epsilon)
				return 0;// no pose

			var targetDist = Vector3.Dot(triangleNormal, target - p0);
			var targetProjected = target - targetDist * triangleNormal;
			var targetCoord = new Barycentric(ref targetProjected, ref p0, ref p1, ref p2);

			pose->v0 = v0;
			pose->v1 = v1;
			pose->v2 = v2;
			pose->area = triangleArea;
			pose->targetDist = targetDist;
			pose->targetCoord = targetCoord;
			return 1;
		}

		public static unsafe int BuildPosesVertex(ref MeshInfo meshInfo, SkinAttachmentPose* pose, ref Vector3 target, int vertex)
		{
			int poseCount = 0;
			foreach (int triangle in meshInfo.meshAdjacency.vertexTriangles[vertex])
			{
				poseCount += BuildPosesTriangle(ref meshInfo, pose + poseCount, ref target, triangle);
			}
			return poseCount;
		}

		//TODO remove
		public unsafe void AttachToTriangle(ref MeshInfo meshInfo, Vector3* targetPositions, int* targetTriangles, int targetCount, int* attachmentIndex, int* attachmentCount)
		{
			var poseIndex = attachData.poseCount;
			var itemIndex = attachData.itemCount;

			fixed (SkinAttachmentPose* pose = attachData.pose)
			fixed (SkinAttachmentItem* item = attachData.item)
			{
				for (int i = 0; i != targetCount; i++)
				{
					var poseCount = BuildPosesTriangle(ref meshInfo, pose + poseIndex, ref targetPositions[i], targetTriangles[i]);
					if (poseCount == 0)
					{
						Debug.LogError("no valid poses for target triangle " + i + ", aborting");
						poseIndex = attachData.poseCount;
						itemIndex = attachData.itemCount;
						break;
					}

					item[itemIndex].poseIndex = poseIndex;
					item[itemIndex].poseCount = poseCount;
					item[itemIndex].baseVertex = meshInfo.meshBuffers.triangles[3 * targetTriangles[i]];
					item[itemIndex].baseNormal = meshInfo.meshBuffers.vertexNormals[item[itemIndex].baseVertex];
					item[itemIndex].targetNormal = item[itemIndex].baseNormal;
					item[itemIndex].targetOffset = Vector3.zero;

					poseIndex += poseCount;
					itemIndex += 1;
				}
			}

			*attachmentIndex = itemIndex > attachData.itemCount ? attachData.itemCount : -1;
			*attachmentCount = itemIndex - attachData.itemCount;

			attachData.poseCount = poseIndex;
			attachData.itemCount = itemIndex;
		}

		public unsafe void AttachToVertex(ref MeshInfo meshInfo, Vector3* targetPositions, Vector3* targetOffsets, Vector3* targetNormals, int* targetVertices, int targetCount, int* attachmentIndex, int* attachmentCount)
		{
			var poseIndex = attachData.poseCount;
			var descIndex = attachData.itemCount;

			fixed (SkinAttachmentPose* pose = attachData.pose)
			fixed (SkinAttachmentItem* desc = attachData.item)
			{
				for (int i = 0; i != targetCount; i++)
				{
					var poseCount = BuildPosesVertex(ref meshInfo, pose + poseIndex, ref targetPositions[i], targetVertices[i]);
					if (poseCount == 0)
					{
						Debug.LogError("no valid poses for target vertex " + i + ", aborting");
						poseIndex = attachData.poseCount;
						descIndex = attachData.itemCount;
						break;
					}

					desc[descIndex].poseIndex = poseIndex;
					desc[descIndex].poseCount = poseCount;
					desc[descIndex].baseVertex = targetVertices[i];
					desc[descIndex].baseNormal = meshInfo.meshBuffers.vertexNormals[targetVertices[i]];
					desc[descIndex].targetNormal = targetNormals[i];
					desc[descIndex].targetOffset = targetOffsets[i];

					poseIndex += poseCount;
					descIndex += 1;
				}
			}

			*attachmentIndex = descIndex > attachData.itemCount ? attachData.itemCount : -1;
			*attachmentCount = descIndex - attachData.itemCount;

			attachData.poseCount = poseIndex;
			attachData.itemCount = descIndex;
		}

		public unsafe void AttachToClosestVertex(ref MeshInfo meshInfo, Vector3* targetPositions, Vector3* targetNormals, int targetCount, int* attachmentIndex, int* attachmentCount)
		{
			using (var targetOffsets = new UnsafeArrayVector3(targetCount))
			using (var targetVertices = new UnsafeArrayInt(targetCount))
			{
				for (int i = 0; i != targetCount; i++)
				{
					targetOffsets.val[i] = Vector3.zero;
					targetVertices.val[i] = meshInfo.meshVertexBSP.FindNearest(ref targetPositions[i]);
				}
				AttachToVertex(ref meshInfo, targetPositions, targetOffsets.val, targetNormals, targetVertices.val, targetCount, attachmentIndex, attachmentCount);
			}
		}

		//---------
		// Resolve

		void ResolveSubjects()
		{
			Profiler.BeginSample("resolve-subj-all");

			subjects.RemoveAll(p => p == null);

			//Profiler.BeginSample("sort");
			//subjects.Sort((a, b) => { return b.attachmentCount.CompareTo(a.attachmentCount); });
			//Profiler.EndSample();

			int stagingPinsSourceDataCount = 3;
			int stagingPinsSourceDataOffset = subjects.Count * 2;

			ArrayUtils.ResizeChecked(ref stagingJobs, subjects.Count);
			ArrayUtils.ResizeChecked(ref stagingData, subjects.Count * 2);
			ArrayUtils.ResizeChecked(ref stagingPins, subjects.Count * 2 + stagingPinsSourceDataCount);

			stagingPins[stagingPinsSourceDataOffset + 0] = GCHandle.Alloc(meshBuffers.vertexPositions, GCHandleType.Pinned);
			stagingPins[stagingPinsSourceDataOffset + 1] = GCHandle.Alloc(meshBuffers.vertexTangents, GCHandleType.Pinned);
			stagingPins[stagingPinsSourceDataOffset + 2] = GCHandle.Alloc(meshBuffers.vertexNormals, GCHandleType.Pinned);

			var targetToWorld = Matrix4x4.TRS(this.transform.position, this.transform.rotation, Vector3.one);
			// NOTE: targetToWorld specifically excludes scale, since source data (BakeMesh) is already scaled

			var targetMeshWorldBounds = meshBakedOrAsset.bounds;
			var targetMeshWorldBoundsCenter = targetMeshWorldBounds.center;
			var targetMeshWorldBoundsExtent = targetMeshWorldBounds.extents;

			for (int i = 0, n = subjects.Count; i != n; i++)
			{
				var subject = subjects[i];

				int attachmentIndex = subject.attachmentIndex;
				int attachmentCount = subject.attachmentCount;
				if (attachmentIndex == -1)
					continue;

				var indexPos = i * 2 + 0;
				var indexNrm = i * 2 + 1;

				ArrayUtils.ResizeChecked(ref stagingData[indexPos], attachmentCount);
				ArrayUtils.ResizeChecked(ref stagingData[indexNrm], attachmentCount);
				stagingPins[indexPos] = GCHandle.Alloc(stagingData[indexPos], GCHandleType.Pinned);
				stagingPins[indexNrm] = GCHandle.Alloc(stagingData[indexNrm], GCHandleType.Pinned);

				unsafe
				{
					var resolvedPositions = (Vector3*)stagingPins[indexPos].AddrOfPinnedObject().ToPointer();
					var resolvedNormals = (Vector3*)stagingPins[indexNrm].AddrOfPinnedObject().ToPointer();
					switch (subject.attachmentType)
					{
						case SkinAttachment.AttachmentType.Transform:
							{
								stagingJobs[i] = ScheduleResolve(attachmentIndex, attachmentCount, ref targetToWorld, resolvedPositions, resolvedNormals);
							}
							break;

						case SkinAttachment.AttachmentType.Mesh:
						case SkinAttachment.AttachmentType.MeshRoots:
							{
								var targetToSubject = subject.transform.worldToLocalMatrix * targetToWorld;
								stagingJobs[i] = ScheduleResolve(attachmentIndex, attachmentCount, ref targetToSubject, resolvedPositions, resolvedNormals);
							}
							break;
					}
				}
			}

			JobHandle.ScheduleBatchedJobs();

			while (true)
			{
				var jobsRunning = false;

				for (int i = 0, n = subjects.Count; i != n; i++)
				{
					var subject = subjects[i];

					var stillRunning = (stagingJobs[i].IsCompleted == false);
					if (stillRunning)
					{
						jobsRunning = true;
						continue;
					}

					var indexPos = i * 2 + 0;
					var indexNrm = i * 2 + 1;

					var alreadyApplied = (stagingPins[indexPos].IsAllocated == false);
					if (alreadyApplied)
						continue;

					stagingPins[indexPos].Free();
					stagingPins[indexNrm].Free();

					Profiler.BeginSample("gather-subj");
					switch (subject.attachmentType)
					{
						case SkinAttachment.AttachmentType.Transform:
							{
								subject.transform.position = stagingData[indexPos][0];
							}
							break;

						case SkinAttachment.AttachmentType.Mesh:
						case SkinAttachment.AttachmentType.MeshRoots:
							{
								if (subject.meshInstance.vertexCount != stagingData[indexPos].Length)
								{
									Debug.LogError("mismatching vertex- and attachment count", subject);
									break;
								}

								subject.meshInstance.EnableSilentWrites(true);
								subject.meshInstance.vertices = stagingData[indexPos];
								subject.meshInstance.normals = stagingData[indexNrm];
								subject.meshInstance.EnableSilentWrites(false);

								//Profiler.BeginSample("recalc-bounds");
								//subject.meshInstance.RecalculateBounds();
								//Profiler.EndSample();

								Profiler.BeginSample("conservative-bounds");
								{
									//Debug.Log("targetMeshWorldBoundsCenter = " + targetMeshWorldBoundsCenter.ToString("G4") + " (from meshBakedOrAsset = " + meshBakedOrAsset.ToString() + ")");
									//Debug.Log("targetMeshWorldBoundsExtents = " + targetMeshWorldBoundsExtents.ToString("G4"));
									var worldToSubject = subject.transform.worldToLocalMatrix;
									var subjectBoundsCenter = worldToSubject.MultiplyPoint(targetMeshWorldBoundsCenter);
									var subjectBoundsRadius = worldToSubject.MultiplyVector(targetMeshWorldBoundsExtent).magnitude + subject.meshAssetRadius;
									var subjectBounds = subject.meshInstance.bounds;
									{
										subjectBounds.center = subjectBoundsCenter;
										subjectBounds.extents = subjectBoundsRadius * Vector3.one;
									}
									subject.meshInstance.bounds = subjectBounds;
								}
								Profiler.EndSample();
							}
							break;
					}
					Profiler.EndSample();
				}

				if (jobsRunning == false)
					break;
			}

			for (int i = 0; i != stagingPinsSourceDataCount; i++)
			{
				stagingPins[stagingPinsSourceDataOffset + i].Free();
			}

			Profiler.EndSample();
		}

		public unsafe JobHandle ScheduleResolve(int attachmentIndex, int attachmentCount, ref Matrix4x4 resolveTransform, Vector3* resolvedPositions, Vector3* resolvedNormals)
		{
			fixed (Vector3* meshPositions = meshBuffers.vertexPositions)
			fixed (Vector3* meshNormals = meshBuffers.vertexNormals)
			fixed (SkinAttachmentItem* attachItem = attachData.item)
			fixed (SkinAttachmentPose* attachPose = attachData.pose)
			{
				var job = new ResolveJob()
				{
					meshPositions = meshPositions,
					meshNormals = meshNormals,
					attachItem = attachItem,
					attachPose = attachPose,
					resolveTransform = resolveTransform,
					resolvedPositions = resolvedPositions,
					resolvedNormals = resolvedNormals,
					attachmentIndex = attachmentIndex,
					attachmentCount = attachmentCount,
				};
				return job.Schedule(attachmentCount, 64);
			}
		}

		[BurstCompile(FloatMode = FloatMode.Fast)]
		unsafe struct ResolveJob : IJobParallelFor
		{
			[NativeDisableUnsafePtrRestriction] public Vector3* meshPositions;
			[NativeDisableUnsafePtrRestriction] public Vector3* meshNormals;
			[NativeDisableUnsafePtrRestriction] public SkinAttachmentItem* attachItem;
			[NativeDisableUnsafePtrRestriction] public SkinAttachmentPose* attachPose;
			[NativeDisableUnsafePtrRestriction] public Vector3* resolvedPositions;
			[NativeDisableUnsafePtrRestriction] public Vector3* resolvedNormals;

			public Matrix4x4 resolveTransform;

			public int attachmentIndex;
			public int attachmentCount;

			//TODO this is still too slow, speed it up
			public void Execute(int i)
			{
				var targetBlended = new Vector3(0.0f, 0.0f, 0.0f);
				var targetWeights = 0.0f;

				SkinAttachmentItem desc = attachItem[attachmentIndex + i];

				var poseIndex0 = desc.poseIndex;
				var poseIndexN = desc.poseIndex + desc.poseCount;

				for (int poseIndex = poseIndex0; poseIndex != poseIndexN; poseIndex++)
				{
					SkinAttachmentPose pose = attachPose[poseIndex];

					var p0 = meshPositions[pose.v0];
					var p1 = meshPositions[pose.v1];
					var p2 = meshPositions[pose.v2];

					var v0v1 = p1 - p0;
					var v0v2 = p2 - p0;

					var triangleNormal = Vector3.Cross(v0v1, v0v2);
					var triangleArea = Vector3.Magnitude(triangleNormal);

					triangleNormal /= triangleArea;
					triangleArea *= 0.5f;

					//var n0 = meshNormals[pose.v0];
					//var n1 = meshNormals[pose.v1];
					//var n2 = meshNormals[pose.v2];

					var targetProjected = pose.targetCoord.Resolve(ref p0, ref p1, ref p2);
					//var targetNormal = pose.targetCoord.Resolve(n0, n1, n2);
					var target = targetProjected + triangleNormal * pose.targetDist;

					//TODO back to orig. area?
					targetBlended += /*pose.area*/triangleArea * target;
					targetWeights += /*pose.area*/triangleArea;
				}

				var targetNormalRot = Quaternion.FromToRotation(desc.baseNormal, meshNormals[desc.baseVertex]);
				var targetNormal = targetNormalRot * desc.targetNormal;
				var targetOffset = targetNormalRot * desc.targetOffset;

				resolvedPositions[i] = resolveTransform.MultiplyPoint3x4(targetBlended / targetWeights + targetOffset);
				resolvedNormals[i] = resolveTransform.MultiplyVector(targetNormal);
			}
		}

		//--------
		// Gizmos

		//PACKAGETODO move these to SkinAttachmentTargetEditor

#if UNITY_EDITOR
		public void OnDrawGizmos()
		{
			var activeGO = UnityEditor.Selection.activeGameObject;
			if (activeGO == null)
				return;
			if (activeGO != this.gameObject && activeGO.GetComponent<SkinAttachment>() == null)
				return;

			Gizmos.matrix = this.transform.localToWorldMatrix;

			if (showWireframe)
			{
				Profiler.BeginSample("show-wire");
				{
					var meshVertexCount = meshBuffers.vertexCount;
					var meshPositions = meshBuffers.vertexPositions;
					var meshNormals = meshBuffers.vertexNormals;

					Gizmos.color = Color.Lerp(Color.clear, Color.green, 0.25f);
					Gizmos.DrawWireMesh(meshBakedOrAsset, 0);

					Gizmos.color = Color.red;
					for (int i = 0; i != meshVertexCount; i++)
					{
						Gizmos.DrawRay(meshPositions[i], meshNormals[i] * 0.001f);// 1mm
					}
				}
				Profiler.EndSample();
			}

			if (showUVSeams)
			{
				Profiler.BeginSample("show-seams");
				{
					Gizmos.color = Color.cyan;
					var weldedAdjacency = new MeshAdjacency(meshBuffers, true);
					for (int i = 0; i != weldedAdjacency.vertexCount; i++)
					{
						if (weldedAdjacency.vertexWelded.GetCount(i) > 0)
						{
							bool seam = false;
							foreach (var j in weldedAdjacency.vertexVertices[i])
							{
								if (weldedAdjacency.vertexWelded.GetCount(j) > 0)
								{
									seam = true;
									if (i < j)
									{
										Gizmos.DrawLine(meshBuffers.vertexPositions[i], meshBuffers.vertexPositions[j]);
									}
								}
							}
							if (!seam)
							{
								Gizmos.color = Color.magenta;
								Gizmos.DrawRay(meshBuffers.vertexPositions[i], meshBuffers.vertexNormals[i] * 0.003f);
								Gizmos.color = Color.cyan;
							}
						}
					}
				}
				Profiler.EndSample();
			}

			if (showResolved)
			{
				Profiler.BeginSample("show-resolve");
				unsafe
				{
					var attachmentIndex = 0;
					var attachmentCount = attachData.itemCount;

					using (var resolvedPositions = new UnsafeArrayVector3(attachmentCount))
					using (var resolvedNormals = new UnsafeArrayVector3(attachmentCount))
					{
						var resolveTransform = Matrix4x4.identity;
						var resolveJob = ScheduleResolve(attachmentIndex, attachmentCount, ref resolveTransform, resolvedPositions.val, resolvedNormals.val);

						JobHandle.ScheduleBatchedJobs();

						resolveJob.Complete();

						Gizmos.color = Color.yellow;
						for (int i = 0; i != attachmentCount; i++)
						{
							Gizmos.DrawSphere(resolvedPositions.val[i], 0.0002f);
						}
					}
				}
				Profiler.EndSample();
			}
		}
#endif
	}
}