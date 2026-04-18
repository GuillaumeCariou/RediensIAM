{{/*
rediensiam.hydraAdminUrl
Returns the Hydra admin API URL:
  - local.enabled=true  → internal cluster service derived from release name
  - local.enabled=false → rediensiam.hydra.external.adminUrl
*/}}
{{- define "rediensiam.hydraAdminUrl" -}}
{{- if .Values.rediensiam.hydra.local.enabled -}}
http://{{ .Release.Name }}-hydra-admin:4445
{{- else -}}
{{ .Values.rediensiam.hydra.external.adminUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.hydraPublicUrl
Returns the Hydra public API URL.
*/}}
{{- define "rediensiam.hydraPublicUrl" -}}
{{- if .Values.rediensiam.hydra.local.enabled -}}
http://{{ .Release.Name }}-hydra-public:4444
{{- else -}}
{{ .Values.rediensiam.hydra.external.publicUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ketoReadUrl
Returns the Keto read API URL.
*/}}
{{- define "rediensiam.ketoReadUrl" -}}
{{- if .Values.rediensiam.keto.local.enabled -}}
http://{{ .Release.Name }}-keto-read:4466
{{- else -}}
{{ .Values.rediensiam.keto.external.readUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ketoWriteUrl
Returns the Keto write API URL.
*/}}
{{- define "rediensiam.ketoWriteUrl" -}}
{{- if .Values.rediensiam.keto.local.enabled -}}
http://{{ .Release.Name }}-keto-write:4467
{{- else -}}
{{ .Values.rediensiam.keto.external.writeUrl }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ingressPublicHost
Returns the public ingress hostname.
Uses rediensiam.ingress.public.host if set; otherwise parses host from rediensiam.publicUrl.
*/}}
{{- define "rediensiam.ingressPublicHost" -}}
{{- if .Values.rediensiam.ingress.public.host -}}
{{ .Values.rediensiam.ingress.public.host }}
{{- else -}}
{{ index (urlParse .Values.rediensiam.publicUrl) "host" }}
{{- end -}}
{{- end -}}

{{/*
rediensiam.ingressAdminHost
Returns the admin ingress hostname.
Uses rediensiam.ingress.admin.host if set; otherwise parses host from rediensiam.adminUrl.
*/}}
{{- define "rediensiam.ingressAdminHost" -}}
{{- if .Values.rediensiam.ingress.admin.host -}}
{{ .Values.rediensiam.ingress.admin.host }}
{{- else -}}
{{ index (urlParse .Values.rediensiam.adminUrl) "host" }}
{{- end -}}
{{- end -}}
