export interface BoundingRegion {
  pageNumber: number;
  polygon: number[];
}

export interface PiiEntity {
  id: string;
  text: string;
  category: string;
  subCategory: string;
  confidenceScore: number;
  occurrenceCount: number;
  boundingRegions: BoundingRegion[];
}

export interface UploadResponse {
  sessionId: string;
  originalFileName: string;
  entities: PiiEntity[];
}

export interface PreviewResponse {
  fileUrl: string;
  hasHighlights: boolean;
  fileType: string;
}
