import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'ADOm8 Onboarding',
  description: 'Portfolio onboarding control plane for ADOm8 projects'
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
