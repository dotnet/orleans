export interface GallerySample {
  slug: string;
  title: string;
  description: string;
  path: string;
  sourceRepository: string;
  image: string | null;
  languages: string[];
  tags: string[];
  featured: boolean;
}

export interface PreparedGallery {
  missing: boolean;
  items: GallerySample[];
}
