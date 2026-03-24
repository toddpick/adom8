const BRAND_LOGO_PATH = '/brand/logo-option-chunky-infinity-box.svg';

export default function BrandLogo({ className = 'h-10 w-10', alt = 'ADOm8 logo' }) {
  return (
    <img
      src={BRAND_LOGO_PATH}
      alt={alt}
      className={`${className} shrink-0 select-none`}
      draggable="false"
    />
  );
}
